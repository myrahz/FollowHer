using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.Shared.Helpers;
using FollowHer.Utils;

namespace FollowHer.Features.Following.Pathfinding;

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
public class PathFollower
{
    /// <summary>A* node budget per computation. This is a *time* limit, not a range limit: the search
    /// runs on the game thread, so it can't flood a whole zone the way Radar's background scan does.
    /// Reach beyond it is covered by best-effort partial paths plus recomputing every
    /// <see cref="MinPathRecomputeIntervalMs"/> as we advance - the frontier moves with the character.
    /// Public because callers that run their own one-off search (e.g. the dash-shortcut detour
    /// measurement) must use the same budget as normal path computation.</summary>
    public const int MaxPathSearchNodes = 40000;

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

    // Jump planning. The provider reports the longest jump (world units) any usable skill can make
    // right now, 0 when none - supplied by whoever owns the skills, so this class stays free of
    // input and skill state.
    private readonly Func<float> _jumpRangeProvider;
    private readonly HashSet<(int x, int y)> _jumpLandings = new();
    private readonly HashSet<(int x, int y)> _bannedLandings = new();
    private float _jumpRangeAtPlan;
    private DateTime? _jumpFirstAttemptAt;

    // How close to the takeoff cell the jump is taken from. Much tighter than the normal waypoint
    // tolerance: a jump aimed from 300 units back would start from the wrong side of the obstacle.
    private const float JumpTakeoffWorldDistance = 45f;

    // Close enough to the landing cell to call the jump done.
    private const float JumpLandedWorldDistance = 80f;

    // A jump that hasn't landed this long after the first attempt didn't work (fell short, blocked
    // by something the grids don't show) - ban that landing and plan around it.
    private const int JumpGiveUpMs = 2500;

    public PathFollower(GameController gameController, LineOfSight lineOfSight, Func<float> jumpRangeProvider = null)
    {
        _gameController = gameController;
        _lineOfSight = lineOfSight;
        _jumpRangeProvider = jumpRangeProvider;
    }

    /// <summary>Call when a jump skill was actually used for the current <see cref="PathStepKind.Jump"/>
    /// step. Starts the clock that bans the landing if the character doesn't arrive.</summary>
    public void ReportJumpAttempt() => _jumpFirstAttemptAt ??= DateTime.Now;

    private float CurrentJumpRange()
    {
        if (_jumpRangeProvider == null || !FollowHer.Instance.Settings.Movement.EnableJumpPathing) return 0f;
        return _jumpRangeProvider();
    }

    public int WaypointCount => _currentPath?.Count ?? 0;
    public int RemainingWaypoints => _currentPath == null ? 0 : Math.Max(0, _currentPath.Count - _currentPathIndex);

    public static (int x, int y) RoundGrid(Vector2 gridPos)
    {
        return ((int)Math.Round(gridPos.X), (int)Math.Round(gridPos.Y));
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
        var grid = _lineOfSight.GetGridReference(LineOfSightDataType.Walkable);
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

        var start = RoundGrid(player.GridPosNum);
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
                var grid = _lineOfSight.GetGridReference(LineOfSightDataType.Walkable);
                if (grid == null)
                {
                    // Terrain for this area hasn't been read yet - there is nothing to path on. The
                    // caller decides what that's worth: following walks straight at a visible leader,
                    // navigation waits.
                    return PathStep.NoTerrainData(targetWorld, targetGrid);
                }

                var jumpRange = CurrentJumpRange();
                GridPathfinder.JumpOptions jumpOptions = null;
                if (jumpRange > 0f)
                {
                    var targetingGrid = _lineOfSight.GetGridReference(LineOfSightDataType.Terrain);
                    if (targetingGrid != null)
                    {
                        jumpOptions = new GridPathfinder.JumpOptions
                        {
                            TargetingGrid = targetingGrid,
                            MaxCells = (int)(jumpRange / GridToWorldMultiplier),
                            Cost = FollowHer.Instance.Settings.Movement.JumpPathCost.Value * 10,
                            BannedLandings = _bannedLandings,
                        };
                    }
                }

                var rawPath = GridPathfinder.AStar(grid, start, target, MaxPathSearchNodes,
                    allowPartialPath: true, out var isPartial,
                    FollowHer.Instance.Settings.Movement.WallClearance.Value,
                    jumpOptions, _jumpLandings);

                _jumpRangeAtPlan = jumpOptions != null ? jumpRange : 0f;
                _jumpFirstAttemptAt = null;
                _currentPath = rawPath is { Count: > 0 } ? SmoothPath(rawPath) : null;
                _currentPathIsPartial = _currentPath != null && isPartial;
                _currentPathIndex = 0;
                _currentPathTarget = target;
                _nextPathRecomputeAt = DateTime.Now.AddMilliseconds(MinPathRecomputeIntervalMs);
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
        var grid = _lineOfSight.GetGridReference(LineOfSightDataType.Walkable);
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
    }

    private bool NeedsPathRecompute((int x, int y) target)
    {
        if (_currentPathTarget == null) return true;
        if (_currentPath == null) return DateTime.Now >= _nextPathRecomputeAt;
        if (_currentPathIndex >= _currentPath.Count) return true;

        // The route was planned while no jump skill was ready; one is now, so a shortcut may exist.
        // Only that direction triggers a replan - a skill going on cooldown mid-route must not throw
        // away a jump we're already walking to, we'd rather wait for it.
        if (_jumpRangeAtPlan <= 0f && DateTime.Now >= _nextPathRecomputeAt && CurrentJumpRange() > 0f) return true;

        var drift = Vector2.Distance(
            new Vector2(target.x, target.y),
            new Vector2(_currentPathTarget.Value.x, _currentPathTarget.Value.y));

        if (drift <= PathRetargetThresholdGrid) return false;

        return DateTime.Now >= _nextPathRecomputeAt;
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
                        (walkableGrid == null || GridPathfinder.HasCorridorClearance(walkableGrid, from, to, corridorMargin)))
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
