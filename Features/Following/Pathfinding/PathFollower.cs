using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.Shared.Helpers;
using FollowHer.Utils;

namespace FollowHer.Features.Following.Pathfinding;

/// <summary>What a <see cref="PathFollower"/> is walking to, which decides how it searches and when it
/// replans.</summary>
public enum PathingMode
{
    /// <summary>A moving target (the leader), normally nearby. Cheap budgeted search, replanned as the
    /// target drifts.</summary>
    Chase,

    /// <summary>A destination that never moves (the target navigator). One whole-zone search, a cached
    /// complete route, replanned only when the character leaves it.</summary>
    Fixed,
}

public enum PathStepKind
{
    /// <summary>Walk to the returned waypoint - the next node on the computed path.</summary>
    Moving,

    /// <summary>Pathfinding is switched off - walk straight at the final target instead.</summary>
    Direct,

    /// <summary>The area's terrain hasn't been read yet (or is being re-read after a zone change), so
    /// there is nothing to path on. Distinct from <see cref="Direct"/>: following still walks straight
    /// at a leader it can see, but a route to a fixed target has to wait rather than set off blind.</summary>
    NoTerrainData,

    /// <summary>Already standing on the target, or the whole path has been walked.</summary>
    Arrived,

    /// <summary>A* found no walkable route to the target.</summary>
    NoPath,

    /// <summary>Standing at the takeoff of a planned jump: use a jump skill aimed at the returned
    /// landing point rather than walking to it.</summary>
    Jump,
}

public readonly struct PathStep
{
    public PathStepKind Kind { get; }
    public Vector3 World { get; }
    public Vector2 Grid { get; }

    /// <summary>True on the step where A* actually ran (as opposed to reusing a cached path), so
    /// callers can log/record the outcome once per computation rather than every tick.</summary>
    public bool PathJustComputed { get; }

    private PathStep(PathStepKind kind, Vector3 world, Vector2 grid, bool pathJustComputed)
    {
        Kind = kind;
        World = world;
        Grid = grid;
        PathJustComputed = pathJustComputed;
    }

    public static PathStep Waypoint(Vector3 world, Vector2 grid, bool justComputed) =>
        new(PathStepKind.Moving, world, grid, justComputed);

    public static PathStep StraightLine(Vector3 world, Vector2 grid) =>
        new(PathStepKind.Direct, world, grid, false);

    public static PathStep NoTerrainData(Vector3 world, Vector2 grid) =>
        new(PathStepKind.NoTerrainData, world, grid, false);

    public static PathStep Arrived(bool justComputed = false) =>
        new(PathStepKind.Arrived, Vector3.Zero, Vector2.Zero, justComputed);

    public static PathStep JumpTo(Vector3 world, Vector2 grid, bool justComputed) =>
        new(PathStepKind.Jump, world, grid, justComputed);

    public static PathStep NoPath(bool justComputed) =>
        new(PathStepKind.NoPath, Vector3.Zero, Vector2.Zero, justComputed);
}

/// <summary>Walks a character along an A* path to an arbitrary grid cell: computes the route, caches
/// it, smooths it, and hands back the next waypoint each tick. Extracted from FollowManager so the
/// leader-follow logic and the manual target navigator (Features/Navigation) drive the *same*
/// pathfinding rather than two copies of it - which is what makes the navigator a real test of what
/// following actually does. It deliberately does not issue any input: the caller decides what to do
/// with the returned step (FollowManager.ExecuteMovement owns movement-skill selection).</summary>
/// <summary>What the configured jump skills can do, for the path planner: the longest jump in world
/// units (0 = none), what a jump is worth to the planner in grid cells, and - when every jump skill
/// always moves at least some distance - that minimum in world units (0 = jumps may be shorter).</summary>
public readonly record struct JumpCapability(float Range, int CostCells, float MinTravelWorld = 0f);

public class PathFollower
{
    // Fixed routes are walked straight along, so the only reason to replan is having left the route.
    // Waypoints are at most Max Waypoint Distance apart, so being further than this from the one
    // we're heading to means we were moved (knockback, a jump that fell short, a teleport).
    private const float FixedOffRouteSlackWorld = 400f;

    // A Fixed search that finds nothing is the whole zone's answer, and costs a few hundred ms to
    // reach, so retrying is much slower than the 400ms used for the cheap follow search.
    private const int FixedNoPathRetryMs = 1000;

    /// <summary>A* node budget per computation. This is a *time* limit, not a range limit: the search
    /// runs on the game thread, so it can't flood a whole zone the way Radar's background scan does.
    /// Reach beyond it is covered by best-effort partial paths plus recomputing every
    /// <see cref="MinPathRecomputeIntervalMs"/> as we advance - the frontier moves with the character.
    /// Public because callers that run their own one-off search (e.g. the dash-shortcut detour
    /// measurement) must use the same budget as normal path computation.</summary>
    public const int MaxPathSearchNodes = 40000;

    /// <summary>Budget for the whole-zone search that replaces a partial route (see GetNextStep).
    /// Effectively "every walkable cell": the search stops when the open set empties long before this.</summary>
    public const int MaxFullSearchNodes = 1_500_000;

    // The whole-zone search is expensive enough to hitch a frame, so it can't run on every recompute
    // while chasing a far-off moving target.
    private const int MinFullSearchIntervalMs = 5000;
    private DateTime _nextFullSearchAt = DateTime.MinValue;

    private const float PathRetargetThresholdGrid = 20f;
    private const int MinPathRecomputeIntervalMs = 400;

    // Line-of-sight probes allowed per waypoint while smoothing - see SmoothPath.
    private const int MaxSmoothingProbes = 8;

    // 1 grid unit = 250/23 world units, so waypoint spacing can be configured in the same world units
    // as every other distance in the plugin rather than in raw grid cells.
    private const float GridToWorldMultiplier = 250f / 23f;

    private readonly GameController _gameController;
    private readonly LineOfSight _lineOfSight;

    private List<(int x, int y)> _currentPath;
    private int _currentPathIndex;
    private (int x, int y)? _currentPathTarget;
    private bool _currentPathIsPartial;
    private DateTime _nextPathRecomputeAt = DateTime.MinValue;

    // Jump planning. The provider reports what the configured jump skills can do - the longest jump
    // (world units) and what a jump is worth to the planner - supplied by whoever owns the skills, so
    // this class stays free of input and skill state. It describes the skills that are set up, not the
    // ones off cooldown this instant: a skill on a 3 s cooldown is still a good plan if it will be back
    // by the time the character reaches the takeoff, and if it isn't, ReportJumpUnavailable replans.
    private readonly Func<JumpCapability> _jumpProvider;
    private readonly HashSet<(int x, int y)> _jumpLandings = new();
    private readonly HashSet<(int x, int y)> _bannedLandings = new();
    private float _jumpRangeAtPlan;
    private DateTime? _jumpFirstAttemptAt;
    private DateTime? _jumpWaitStartedAt;
    private DateTime _jumpsSuppressedUntil = DateTime.MinValue;

    // How close to the takeoff cell the jump is taken from. Much tighter than the normal waypoint
    // tolerance: a jump aimed from 300 units back would start from the wrong side of the obstacle.
    private const float JumpTakeoffWorldDistance = 45f;

    // Extra room kept between a planned jump's length and the skill's reach: the landing is a cell
    // centre, the character stands somewhere within the takeoff tolerance, and distances are rounded.
    private const float JumpReachMarginWorld = 15f;

    // Close enough to the landing cell to call the jump done.
    private const float JumpLandedWorldDistance = 80f;

    // A jump that hasn't landed this long after the first attempt didn't work (fell short, blocked
    // by something the grids don't show) - ban that landing and plan around it.
    private const int JumpGiveUpMs = 2500;

    // After giving up on a jump because its skill never came off cooldown, plan without jumps for this
    // long. Banning only the one landing would just pick the next cell along the same gap.
    private const int JumpSuppressMs = 8000;

    private readonly PathingMode _mode;

    public PathFollower(GameController gameController, LineOfSight lineOfSight, PathingMode mode,
        Func<JumpCapability> jumpProvider = null)
    {
        _gameController = gameController;
        _lineOfSight = lineOfSight;
        _mode = mode;
        _jumpProvider = jumpProvider;
    }

    /// <summary>Call when a jump skill was actually used for the current <see cref="PathStepKind.Jump"/>
    /// step. Starts the clock that bans the landing if the character doesn't arrive.</summary>
    public void ReportJumpAttempt()
    {
        _jumpFirstAttemptAt ??= DateTime.Now;
        _jumpWaitStartedAt = null;
    }

    /// <summary>Call while standing at a takeoff with no skill that can make the jump ready. Gives the
    /// skill up to <paramref name="waitBudgetMs"/> to come off cooldown; after that the jump is dropped
    /// and the route replanned on foot (jumps stay off for a few seconds so it doesn't just plan the
    /// same gap again). Returns whether it gave up - the caller should then ask for a new step.</summary>
    public bool ReportJumpUnavailable(int waitBudgetMs)
    {
        _jumpWaitStartedAt ??= DateTime.Now;
        if ((DateTime.Now - _jumpWaitStartedAt.Value).TotalMilliseconds < waitBudgetMs) return false;

        if (_currentPath != null && _currentPathIndex < _currentPath.Count)
        {
            _bannedLandings.Add(_currentPath[_currentPathIndex]);
        }

        _jumpsSuppressedUntil = DateTime.Now.AddMilliseconds(JumpSuppressMs);
        _currentPath = null;
        _currentPathTarget = null;
        _jumpWaitStartedAt = null;
        _jumpFirstAttemptAt = null;
        _nextPathRecomputeAt = DateTime.MinValue;
        return true;
    }

    private JumpCapability CurrentJump()
    {
        if (_jumpProvider == null || !FollowHer.Instance.Settings.Movement.EnableJumpPathing) return default;
        if (DateTime.Now < _jumpsSuppressedUntil) return default;
        return _jumpProvider();
    }

    private float CurrentJumpRange() => CurrentJump().Range;

    private int[][] _doorGrid;
    private int[][] _doorGridSource;
    private DateTime _doorGridBuiltAt = DateTime.MinValue;
    private const int DoorGridMaxAgeMs = 20000;

    /// <summary>The walkable grid routes are planned on. With Open Doors on it is the game's pathfinding
    /// data with closed doors cleared - the same view Radar plans on - so a closed door is a way through
    /// (to be clicked open on arrival) rather than a wall. Rebuilt at most every 20 s and per area; line
    /// of sight and skill clearance keep reading the raw grid, so a movement skill still sees the door.</summary>
    private int[][] PlanningGrid()
    {
        var raw = _lineOfSight.GetGridReference(LineOfSightDataType.Walkable);
        if (raw == null || !FollowHer.Instance.Settings.Movement.OpenDoors) return raw;

        if (_doorGrid != null && ReferenceEquals(_doorGridSource, raw) &&
            (DateTime.Now - _doorGridBuiltAt).TotalMilliseconds < DoorGridMaxAgeMs)
        {
            return _doorGrid;
        }

        try
        {
            var cleared = _gameController.IngameState.Data.GetClearedPathfindingData();
            if (cleared != null && cleared.Length == raw.Length)
            {
                _doorGrid = cleared;
                _doorGridSource = raw;
                _doorGridBuiltAt = DateTime.Now;
                return cleared;
            }
        }
        catch
        {
            // Fall back to the raw grid: doors are walls, as before.
        }

        return raw;
    }

    /// <summary>The next few waypoints of the current route, in world coordinates - what lies along
    /// the line the character is about to walk.</summary>
    public List<Vector3> UpcomingWaypoints(int count)
    {
        var result = new List<Vector3>();
        if (_currentPath == null) return result;

        for (var i = _currentPathIndex; i < _currentPath.Count && result.Count < count; i++)
        {
            result.Add(GridToWorld(_currentPath[i].x, _currentPath[i].y));
        }

        return result;
    }

    /// <summary>Where the route ahead first crosses a cell that is blocked on the raw terrain grid,
    /// looking at the next <paramref name="maxNodes"/> waypoints - or null if it doesn't. Routes are
    /// planned on the door-cleared grid, so a cell that is passable there but blocked here is a closed
    /// door standing in the way; that is the only time a door needs opening. Jump edges are skipped:
    /// they cross unwalkable ground on purpose.</summary>
    public Vector3? FirstBlockedCellOnRoute(int maxNodes)
    {
        var raw = _lineOfSight.GetGridReference(LineOfSightDataType.Walkable);
        if (raw == null || _currentPath == null) return null;

        var from = StandingCell();
        var last = Math.Min(_currentPath.Count, _currentPathIndex + maxNodes);
        for (var i = _currentPathIndex; i < last; i++)
        {
            var to = _currentPath[i];
            if (!_jumpLandings.Contains(to))
            {
                var steps = Math.Max(Math.Abs(to.x - from.x), Math.Abs(to.y - from.y));
                for (var s = 0; s <= steps; s++)
                {
                    var t = steps == 0 ? 0f : s / (float)steps;
                    var x = (int)Math.Round(from.x + (to.x - from.x) * t);
                    var y = (int)Math.Round(from.y + (to.y - from.y) * t);
                    if (!GridPathfinder.IsCellWalkable(raw, x, y)) return GridToWorld(x, y);
                }
            }

            from = to;
        }

        return null;
    }

    public int WaypointCount => _currentPath?.Count ?? 0;
    public int RemainingWaypoints => _currentPath == null ? 0 : Math.Max(0, _currentPath.Count - _currentPathIndex);

    public static (int x, int y) RoundGrid(Vector2 gridPos)
    {
        return ((int)Math.Round(gridPos.X), (int)Math.Round(gridPos.Y));
    }

    /// <summary>The grid cell the player is standing on, as a cell A* can start from. Plain rounding
    /// isn't enough: right on the edge of an obstacle the rounded cell can be the blocked one next to
    /// the cell actually underfoot (line of sight truncates instead of rounding, so the two disagree
    /// there), and A* refuses to start from a blocked cell - which reads as "no path" and sends
    /// following walking straight into the obstacle. So if the rounded cell is blocked, use the
    /// truncated one, then the nearest walkable cell.</summary>
    public (int x, int y) StandingCell()
    {
        var position = _gameController.Player.GridPosNum;
        var rounded = RoundGrid(position);

        var grid = _lineOfSight.GetGridReference(LineOfSightDataType.Walkable);
        if (grid == null || GridPathfinder.IsCellWalkable(grid, rounded.x, rounded.y)) return rounded;

        var truncated = ((int)position.X, (int)position.Y);
        if (GridPathfinder.IsCellWalkable(grid, truncated.Item1, truncated.Item2)) return truncated;

        return SnapToWalkable(rounded, 3) ?? rounded;
    }

    public Vector3 GridToWorld(int x, int y)
    {
        var gridPos = new Vector2(x, y);
        var worldXy = gridPos.GridToWorld();
        var z = _gameController.IngameState.Data.GetTerrainHeightAt(gridPos);
        return new Vector3(worldXy.X, worldXy.Y, z);
    }

    /// <summary>Whether A* can find any walkable route between two grid cells. A one-off query that
    /// doesn't touch the cached path.</summary>
    public bool HasWalkablePath((int x, int y) start, (int x, int y) target)
    {
        var grid = PlanningGrid();
        if (grid == null) return false;

        var path = GridPathfinder.AStar(grid, start, target, MaxPathSearchNodes);
        return path is { Count: > 0 };
    }

    /// <summary>The next thing to do to get to <paramref name="target"/>. Waypoints closer than
    /// <paramref name="waypointArrivalWorldDistance"/> are treated as reached and skipped.</summary>
    public PathStep GetNextStep((int x, int y) target, float waypointArrivalWorldDistance)
    {
        var player = _gameController.Player;
        if (player == null) return PathStep.NoPath(false);

        var start = StandingCell();
        if (start == target) return PathStep.Arrived();

        var targetWorld = GridToWorld(target.x, target.y);
        var targetGrid = new Vector2(target.x, target.y);

        if (!FollowHer.Instance.Settings.Movement.EnablePathfindingFallback)
        {
            // A* disabled - always walk straight at the (possibly obstructed) target.
            return PathStep.StraightLine(targetWorld, targetGrid);
        }

        var justComputed = false;

        // Two attempts at most: if the cached route turns out to be a partial one we've now walked to
        // the end of, recompute from where we are (the search reaches further from here) instead of
        // reporting an arrival at a target we never got to.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var forceRecompute = attempt > 0;
            if (forceRecompute || NeedsPathRecompute(target))
            {
                // The grid is only fetched when A* is about to run: LineOfSight.GetGrid copies the
                // whole area grid, so doing it on every tick (as the pre-extraction code did) was
                // pure waste.
                var grid = PlanningGrid();
                if (grid == null)
                {
                    // Terrain for this area hasn't been read yet - there is nothing to path on. The
                    // caller decides what that's worth: following walks straight at a visible leader,
                    // navigation waits.
                    return PathStep.NoTerrainData(targetWorld, targetGrid);
                }

                var jump = CurrentJump();
                var jumpRange = jump.Range;
                GridPathfinder.JumpOptions jumpOptions = null;
                if (jumpRange > 0f)
                {
                    var targetingGrid = _lineOfSight.GetGridReference(LineOfSightDataType.Terrain);
                    if (targetingGrid != null)
                    {
                        jumpOptions = new GridPathfinder.JumpOptions
                        {
                            TargetingGrid = targetingGrid,
                            // The character is only required to stand within JumpTakeoffWorldDistance of
                            // the takeoff cell, so it may be that much further from the landing than the
                            // planner assumed. Plan for the skill's reach minus that slack (and a little
                            // more), or a jump at the very edge of its range is offered, the character
                            // stops a few units short, no skill reaches, and it waits there forever.
                            MaxCells = (int)((jumpRange - JumpTakeoffWorldDistance - JumpReachMarginWorld) / GridToWorldMultiplier),
                            MinCells = jump.MinTravelWorld > 0f ? (int)Math.Round(jump.MinTravelWorld / GridToWorldMultiplier) : 0,
                            Cost = jump.CostCells * 10,
                            BannedLandings = _bannedLandings,
                        };
                    }
                }

                var wallClearance = FollowHer.Instance.Settings.Movement.WallClearance.Value;
                var turnPenalty = FollowHer.Instance.Settings.Movement.PathStraightness.Value * GridPathfinder.TurnPenaltyPerLevel;
                List<(int x, int y)> rawPath;
                bool isPartial;

                if (_mode == PathingMode.Fixed)
                {
                    // A destination that never moves is worth solving properly, once: search the whole
                    // zone, cache the complete route, and walk it. Nothing about the target changes, so
                    // it only gets replanned when we leave the route (see NeedsPathRecompute). A null
                    // here is authoritative - the target really is cut off.
                    rawPath = GridPathfinder.AStar(grid, start, target, MaxFullSearchNodes,
                        allowPartialPath: true, out isPartial, wallClearance, jumpOptions, _jumpLandings, turnPenalty);
                }
                else
                {
                    rawPath = GridPathfinder.AStar(grid, start, target, MaxPathSearchNodes,
                        allowPartialPath: true, out isPartial, wallClearance, jumpOptions, _jumpLandings, turnPenalty);

                    // A leader is normally close, so the cheap search is enough - and it has to be cheap,
                    // because it reruns as they move. But a partial route is only "the explored cell
                    // nearest the target in a straight line", which in a winding level is routinely a
                    // dead end (walls between you and a leader a few dozen cells away that you can only
                    // reach by a long way round). Rather than walk into it, pay for a whole-zone search -
                    // rate-limited, since it can hitch a frame and the target keeps moving.
                    if (isPartial && DateTime.Now >= _nextFullSearchAt)
                    {
                        _nextFullSearchAt = DateTime.Now.AddMilliseconds(MinFullSearchIntervalMs);
                        rawPath = GridPathfinder.AStar(grid, start, target, MaxFullSearchNodes,
                            allowPartialPath: true, out isPartial, wallClearance, jumpOptions, _jumpLandings, turnPenalty);
                    }
                }

                _jumpRangeAtPlan = jumpOptions != null ? jumpRange : 0f;
                _jumpFirstAttemptAt = null;
                _currentPath = rawPath is { Count: > 0 } ? SmoothPath(rawPath) : null;
                _currentPathIsPartial = _currentPath != null && isPartial;
                _currentPathIndex = 0;
                _currentPathTarget = target;
                _nextPathRecomputeAt = DateTime.Now.AddMilliseconds(
                    _mode == PathingMode.Fixed && _currentPath == null ? FixedNoPathRetryMs : MinPathRecomputeIntervalMs);
                justComputed = true;
            }

            if (_currentPath == null) return PathStep.NoPath(justComputed);

            while (_currentPathIndex < _currentPath.Count)
            {
                var node = _currentPath[_currentPathIndex];
                var nodeWorld = GridToWorld(node.x, node.y);
                var distance = Vector3.Distance(player.PosNum, nodeWorld);

                if (_jumpLandings.Contains(node))
                {
                    // Every earlier waypoint is behind us, so we're at the takeoff. Landing is
                    // judged tightly; the usual tolerance would count the jump as done before it
                    // was ever made.
                    if (distance <= JumpLandedWorldDistance)
                    {
                        _jumpFirstAttemptAt = null;
                        _jumpWaitStartedAt = null;
                        _currentPathIndex++;
                        continue;
                    }

                    if (_jumpFirstAttemptAt is { } startedAt &&
                        (DateTime.Now - startedAt).TotalMilliseconds > JumpGiveUpMs)
                    {
                        // Tried and didn't get there. Plan again without this landing instead of
                        // casting at it forever.
                        _bannedLandings.Add(node);
                        _currentPath = null;
                        _currentPathTarget = null;
                        _jumpFirstAttemptAt = null;
                        return GetNextStep(target, waypointArrivalWorldDistance);
                    }

                    return PathStep.JumpTo(nodeWorld, new Vector2(node.x, node.y), justComputed);
                }

                // The waypoint before a jump is the takeoff, which has to be reached properly.
                var isTakeoff = _currentPathIndex + 1 < _currentPath.Count &&
                                _jumpLandings.Contains(_currentPath[_currentPathIndex + 1]);
                var tolerance = isTakeoff ? JumpTakeoffWorldDistance : waypointArrivalWorldDistance;

                if (distance <= tolerance)
                {
                    _currentPathIndex++;
                    continue;
                }

                return PathStep.Waypoint(nodeWorld, new Vector2(node.x, node.y), justComputed);
            }

            // Walked the whole route. If it actually led to the target, that's an arrival; a partial
            // one only reached the search budget's edge, so go round once more and extend it. A
            // partial route that's *already* exhausted the moment it was computed can't be extended
            // any further this tick, so it stops here too.
            if (!_currentPathIsPartial || forceRecompute) return PathStep.Arrived(justComputed);
        }

        return PathStep.Arrived(justComputed);
    }

    /// <summary>What the walkable grid says about a cell, in words - "value 5 (walkable)", "value 0
    /// (blocked)", "outside the 2048x1771 grid". Every "it won't path there" report should carry this:
    /// blocked, out of bounds and no-terrain-loaded are three different faults that look identical
    /// from the outside, and the coordinates a target came from are only meaningful next to the size
    /// of the grid they're being looked up in.</summary>
    public string DescribeCell((int x, int y) cell)
    {
        return DescribeCell(_lineOfSight.GetGridReference(LineOfSightDataType.Walkable), cell);
    }

    /// <summary>The same cell as read from the terrain *Radar* pathfinds on - RawPathfindingData with
    /// triggerable blockades cleared (its Clear Triggerable Blockades option, on by default) - rather
    /// than the raw grid we use. When the two disagree, that difference is the whole answer to "Radar
    /// draws a line there but we won't path to it". Rebuilds the entire grid, so this is for one-off
    /// failure reports only, never per frame.</summary>
    public string DescribeCellOnRadarGrid((int x, int y) cell)
    {
        try
        {
            return DescribeCell(_gameController.IngameState.Data.GetClearedPathfindingData(), cell);
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    private static string DescribeCell(int[][] grid, (int x, int y) cell)
    {
        if (grid == null) return "no terrain data loaded";

        var height = grid.Length;
        if (cell.y < 0 || cell.y >= height || grid[cell.y] == null)
        {
            return $"outside the grid (which is {(height > 0 ? grid[0]?.Length ?? 0 : 0)} x {height})";
        }

        var width = grid[cell.y].Length;
        if (cell.x < 0 || cell.x >= width) return $"outside the grid (which is {width} x {height})";

        var value = grid[cell.y][cell.x];
        return value == 0 ? "value 0 (blocked)" : $"value {value} (walkable)";
    }

    /// <summary>Nudges a destination onto walkable ground, returning null if there's none within
    /// <paramref name="maxRadius"/> cells. A* rejects an unwalkable target cell outright, however
    /// close it is, and a marker's cell isn't guaranteed to be walkable on this grid - Radar picks it
    /// on its own copy of the terrain, which (with its Clear Triggerable Blockades option on, the
    /// default) has openable doors and blockades already cleared.</summary>
    public (int x, int y)? SnapToWalkable((int x, int y) cell, int maxRadius = 12)
    {
        var grid = PlanningGrid();
        if (grid == null) return cell;

        if (GridPathfinder.IsCellWalkable(grid, cell.x, cell.y)) return cell;

        for (var radius = 1; radius <= maxRadius; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    // Only the ring at this radius - inner ones were covered by earlier passes.
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;

                    if (GridPathfinder.IsCellWalkable(grid, cell.x + dx, cell.y + dy))
                    {
                        return (cell.x + dx, cell.y + dy);
                    }
                }
            }
        }

        return null;
    }

    public void Reset()
    {
        _currentPath = null;
        _currentPathIndex = 0;
        _currentPathTarget = null;
        _currentPathIsPartial = false;
        _nextPathRecomputeAt = DateTime.MinValue;
        _jumpLandings.Clear();
        _bannedLandings.Clear();
        _jumpRangeAtPlan = 0f;
        _jumpFirstAttemptAt = null;
        _jumpWaitStartedAt = null;
        _jumpsSuppressedUntil = DateTime.MinValue;
        _nextFullSearchAt = DateTime.MinValue;
    }

    private bool NeedsPathRecompute((int x, int y) target)
    {
        return _mode == PathingMode.Fixed ? NeedsFixedRecompute(target) : NeedsChaseRecompute(target);
    }

    /// <summary>A fixed destination never moves, so a complete cached route stays valid until the
    /// character leaves it - there is no drift to chase and no reason to re-search a whole zone on a
    /// timer.</summary>
    private bool NeedsFixedRecompute((int x, int y) target)
    {
        if (_currentPathTarget == null || _currentPathTarget.Value != target) return true;

        // Nothing found: the search was whole-zone, so retrying only matters if the terrain was still
        // loading. Slowly, then, since each try is expensive.
        if (_currentPath == null) return DateTime.Now >= _nextPathRecomputeAt;

        // Walked off the end. Complete route = arrived (the caller reports it); partial = the search
        // budget ran out before the target, so go again from here.
        if (_currentPathIndex >= _currentPath.Count) return _currentPathIsPartial;

        if (JumpSetupChanged()) return true;

        // Off the route: knocked back, teleported, or a jump that fell short. Not checked while the
        // next node is a jump landing - the takeoff is legitimately a whole jump away from it.
        var node = _currentPath[_currentPathIndex];
        if (!_jumpLandings.Contains(node) && DateTime.Now >= _nextPathRecomputeAt)
        {
            var maxWaypointWorld = FollowHer.Instance.Settings.Movement.MaxWaypointDistance.Value;
            var distance = Vector3.Distance(_gameController.Player.PosNum, GridToWorld(node.x, node.y));
            if (distance > maxWaypointWorld + FixedOffRouteSlackWorld) return true;
        }

        return false;
    }

    /// <summary>Whether the jump skills available now differ from what the route was planned with, so
    /// the route should be planned again while walking it: a jump skill appeared (a shortcut may exist
    /// now), or the reach shrank or vanished (skill disabled, swapped off the bar, left for a town, jumps
    /// switched off) and the route may lead to a jump nothing can make. A skill merely being on cooldown
    /// is not a change - that is waited out at the takeoff, within a budget.</summary>
    private bool JumpSetupChanged()
    {
        if (DateTime.Now < _nextPathRecomputeAt) return false;

        var range = CurrentJumpRange();
        if (_jumpRangeAtPlan <= 0f) return range > 0f;

        // Smaller than planned: the planned jumps may be out of reach now. (A little slack for the
        // rounding in how the range is derived.)
        return range < _jumpRangeAtPlan - 1f;
    }

    private bool NeedsChaseRecompute((int x, int y) target)
    {
        if (_currentPathTarget == null) return true;
        if (_currentPath == null) return DateTime.Now >= _nextPathRecomputeAt;
        if (_currentPathIndex >= _currentPath.Count) return true;

        if (JumpSetupChanged()) return true;

        var drift = Vector2.Distance(
            new Vector2(target.x, target.y),
            new Vector2(_currentPathTarget.Value.x, _currentPathTarget.Value.y));

        if (drift <= PathRetargetThresholdGrid) return false;

        return DateTime.Now >= _nextPathRecomputeAt;
    }

    /// <summary>Whether every cell along the straight line between two cells reads at least
    /// <paramref name="floor"/> on the walkable layer (5 = open floor, 1-4 = margin cells beside walls).
    /// The two end cells are exempt: a waypoint that has to sit on a margin cell may, and only the stretch
    /// between waypoints is being judged.</summary>
    private static bool KeepsToFloor(int[][] grid, Vector2 from, Vector2 to, int floor)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) * 2f));
        for (var s = 1; s < steps; s++)
        {
            var point = from + (to - from) * (s / (float)steps);
            var x = (int)Math.Round(point.X);
            var y = (int)Math.Round(point.Y);
            if (y < 0 || y >= grid.Length || x < 0 || x >= grid[y].Length || grid[y][x] < floor) return false;
        }

        return true;
    }

    /// <summary>Turns A*'s cell-by-cell route into waypoints, by keeping only the furthest cell each
    /// waypoint still has a clear walkable line to. The spacing that falls out of this is the point:
    /// a straight stretch collapses to one long hop (which is what makes a hop long enough for a
    /// movement skill to be worth firing), while a corner keeps its cells because the line breaks
    /// there. How far it may reach is Max Waypoint Distance.</summary>
    private List<(int x, int y)> SmoothPath(List<(int x, int y)> rawPath)
    {
        var maxWaypointWorld = FollowHer.Instance.Settings.Movement.MaxWaypointDistance.Value;
        var maxLookaheadCells = Math.Max(1, (int)Math.Round(maxWaypointWorld / GridToWorldMultiplier));

        // A* already steers off walls, but smoothing would undo it: a straight shortcut between two
        // well-placed cells can still graze a corner. So a shortcut must also have a clear corridor
        // either side (the same check ground dashes use), leaving the tight raw cells at corners.
        var clearance = FollowHer.Instance.Settings.Movement.WallClearance.Value;
        var corridorMargin = clearance > 1 ? clearance - 1 : 0;
        var walkableGrid = corridorMargin > 0 ? _lineOfSight.GetGridReference(LineOfSightDataType.Walkable) : null;

        // A shortcut that grazes the margin cells beside a wall is a straight line along which a ground
        // dash that needs open floor can never fire, however clear the route's own cells were. So when
        // a travel skill requires a floor value, a shortcut must keep to it too; where the route itself
        // has to squeeze past, the waypoints stay close and the shortcuts shorten.
        var floor = FollowHer.Instance.Settings.Movement.MovementSkills.Content
            .Where(skill => skill.Enabled && skill.Name != "Move" && !skill.TravelsThroughObstacles &&
                            skill.UseForTravel && skill.UsableWithCurrentWeapon)
            .Select(skill => skill.RequiredFloorValue.Value)
            .DefaultIfEmpty(0)
            .Max();
        var floorGrid = floor > 1 ? _lineOfSight.GetGridReference(LineOfSightDataType.Walkable) : null;

        var smoothed = new List<(int x, int y)>();
        var i = 0;

        while (i < rawPath.Count)
        {
            var best = i;
            var furthest = Math.Min(rawPath.Count - 1, i + maxLookaheadCells);

            // A jump is not a walkable line, so smoothing must neither merge across one nor drop its
            // endpoints: a landing is always kept, and a hop stops at the takeoff (the cell before
            // the next landing).
            if (_jumpLandings.Contains(rawPath[i]))
            {
                smoothed.Add(rawPath[i]);
                i++;
                continue;
            }

            for (var k = i + 1; k <= furthest; k++)
            {
                if (_jumpLandings.Contains(rawPath[k]))
                {
                    furthest = k - 1;
                    break;
                }
            }

            // Probe from the furthest candidate backwards, but a bounded number of times: each probe
            // is a full ray, and at a large waypoint distance a per-cell walk back would make
            // smoothing cost grow with the square of the setting. Coarse steps can settle for a
            // slightly shorter hop than the true maximum, which costs nothing that matters here.
            var span = furthest - i;
            var step = Math.Max(1, (span + MaxSmoothingProbes - 1) / MaxSmoothingProbes);

            for (var j = furthest; j > i; j -= step)
            {
                try
                {
                    var from = new Vector2(rawPath[i].x, rawPath[i].y);
                    var to = new Vector2(rawPath[j].x, rawPath[j].y);
                    if (_lineOfSight.HasLineOfSight(from, to, LineOfSightDataType.Walkable) &&
                        (walkableGrid == null || GridPathfinder.HasCorridorClearance(walkableGrid, from, to, corridorMargin)) &&
                        (floorGrid == null || KeepsToFloor(floorGrid, from, to, floor)))
                    {
                        best = j;
                        break;
                    }
                }
                catch
                {
                    // ignore LOS errors and keep best = i
                }
            }

            smoothed.Add(rawPath[best]);
            i = best + 1;
        }

        return smoothed;
    }
}
