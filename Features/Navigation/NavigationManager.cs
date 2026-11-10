using System;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FollowHer.Features.Following;
using FollowHer.Features.Following.Pathfinding;

namespace FollowHer.Features.Navigation;

/// <summary>Walks the character to a manually chosen Radar target using the exact pathfinding the
/// leader-follow logic uses (<see cref="PathFollower"/> -> <see cref="GridPathfinder"/> A* over the
/// walkable grid, with the same smoothing, waypoint advancing and movement-skill selection). That's
/// the point of the feature: picking a target on the far side of a zone and watching it get there is
/// a repeatable test of the follow pathfinder, without needing a second client running a leader.
///
/// While a destination is set it takes priority over following the leader - it's a manual override,
/// so it stays in control until it arrives, fails, or is stopped.</summary>
public class NavigationManager
{
    // A* failing once can be transient (terrain data mid-refresh), but navigation searches the whole
    // zone, so a failure is a real answer and each retry is expensive. Failing this many computations
    // in a row means it's genuinely unreachable - report that instead of retrying until the timeout.
    // Retries are one second apart, so this is a few seconds of trying.
    private const int MaxConsecutiveNoPath = 4;

    private readonly GameController _gameController;
    private readonly FollowManager _followManager;
    private readonly PathFollower _pathFollower;

    private DateTime _deadline = DateTime.MaxValue;
    private int _consecutiveNoPath;

    // Stuck recovery: how long the character has been pinned in one place while it should be going
    // somewhere, and the short detour taken to get out of it.
    private readonly StuckWatchdog _stuck = new();
    private DateTime _sidestepUntil = DateTime.MinValue;
    private Vector3 _sidestepWorld;
    private Vector2 _sidestepGrid;
    private const double StuckDiagnoseSeconds = 1.2;
    private const double StuckWalkOnlySeconds = 2.5;
    private const double StuckSidestepSeconds = 4.0;
    private const double StuckGiveUpSeconds = 9.0;

    public NavigationManager(GameController gameController, FollowManager followManager, PathFollower pathFollower)
    {
        _gameController = gameController;
        _followManager = followManager;
        _pathFollower = pathFollower;
    }

    public NavTarget? ActiveTarget { get; private set; }

    /// <summary>The last target picked, kept after stopping or arriving so it can be resumed without
    /// hunting for it in the list again. Cleared on area change - its coordinates are per-area.</summary>
    public NavTarget? LastTarget { get; private set; }

    public bool IsActive => ActiveTarget != null;

    public bool CanResume => LastTarget != null;

    /// <summary>Live progress while navigating, or the outcome of the last run once it's over.</summary>
    public string Status { get; private set; } = "idle";

    /// <summary>Waypoints left on the current route - a quick read on what the pathfinder produced.</summary>
    public int RemainingWaypoints => _pathFollower.RemainingWaypoints;

    /// <summary>What our walkable grid says about a target's cell - for the navigator's tooltips.</summary>
    public string DescribeTargetCell((int x, int y) cell) => _pathFollower.DescribeCell(cell);

    /// <summary>Picks the target the Move Key (and the window's "Walk to selected" button) will walk
    /// to, without starting a walk. Picking a different one while walking just changes what a later
    /// start will use - it doesn't redirect the walk in progress.</summary>
    public void Select(NavTarget target)
    {
        LastTarget = target;
        if (!IsActive) Status = $"selected {target.Label}";
    }

    public void Start(NavTarget target)
    {
        // Remembered even if the start below refuses: the reason may be where we're standing, so
        // retrying the same target after moving is a reasonable thing to want.
        LastTarget = target;
        _pathFollower.Reset();

        // Radar picks a marker's cell on its own copy of the terrain, which can differ from ours
        // (its Clear Triggerable Blockades option, on by default, opens doors and blockades first).
        // A destination that isn't walkable here fails instantly at any distance, so move it onto the
        // nearest cell that is.
        var targetCell = PathFollower.RoundGrid(target.GridPos);
        var snapped = _pathFollower.SnapToWalkable(targetCell);
        if (snapped == null)
        {
            var playerCell = _gameController.Player != null
                ? PathFollower.RoundGrid(_gameController.Player.GridPosNum)
                : (x: 0, y: 0);

            ActiveTarget = null;
            Status = $"{target.Label}: grid ({targetCell.x},{targetCell.y}) is " +
                     $"{_pathFollower.DescribeCell(targetCell)}, and so is every cell within 12 of it. " +
                     $"You are at ({playerCell.x},{playerCell.y}) - {_pathFollower.DescribeCell(playerCell)}.";

            // Logged rather than shown: it rebuilds the whole grid, and the window redraws the status
            // line every frame. Says whether Radar's terrain disagrees with ours about that cell.
            _followManager.LogDebug($"Navigation refused: {Status} " +
                                    $"On Radar's terrain that cell is {_pathFollower.DescribeCellOnRadarGrid(targetCell)}.");
            return;
        }

        target = target with { GridPos = new Vector2(snapped.Value.x, snapped.Value.y) };

        ActiveTarget = target;
        Status = $"heading to {target.Label}";
        _consecutiveNoPath = 0;

        var timeoutSeconds = FollowHer.Instance.Settings.Navigation.Timeout.Value;
        _deadline = timeoutSeconds > 0 ? DateTime.Now.AddSeconds(timeoutSeconds) : DateTime.MaxValue;

        _followManager.LogDebug($"Navigating to '{target.Label}' at grid ({target.GridPos.X:F0},{target.GridPos.Y:F0})");
    }

    /// <summary>Walks to <see cref="LastTarget"/> again - the hotkey/button counterpart to
    /// <see cref="Stop"/>. Restarts from scratch rather than continuing a saved route, so it picks up
    /// wherever the character is standing now.</summary>
    public bool Resume()
    {
        if (LastTarget is not { } target)
        {
            Status = "nothing to resume - pick a target in the navigator first";
            return false;
        }

        Start(target);
        return IsActive;
    }

    /// <summary>Area change: stop and forget the target. Grid coordinates only mean anything within
    /// the area they came from, so resuming one across a zone would walk to an unrelated spot.</summary>
    public void ResetForArea()
    {
        Stop("cancelled - area changed");
        LastTarget = null;
    }

    public void Stop(string status)
    {
        if (ActiveTarget != null)
        {
            _followManager.LogDebug($"Navigation to '{ActiveTarget.Value.Label}' ended: {status}");
        }

        ActiveTarget = null;
        Status = status;
        _consecutiveNoPath = 0;
        _deadline = DateTime.MaxValue;
        _pathFollower.Reset();
        _stuck.Reset();
        _sidestepUntil = DateTime.MinValue;
    }

    /// <summary>Issues one movement input toward a waypoint. ExecuteMovement declines when the point
    /// projects to nothing on screen (WorldToScreen returns zero, e.g. it's behind the camera); that's
    /// a "couldn't move this frame", not a "navigation isn't running", so it only changes the status
    /// text. Letting it escape as a false return is what used to hand the tick to the follow logic
    /// while a destination was still set.</summary>
    private void Move(Vector3 world, Vector2 grid, string status)
    {
        Status = _followManager.ExecuteMovement(world, grid)
            ? status
            : $"{status} - waiting for it to come on screen";
    }

    /// <summary>Escalating answer to a character that is pinned in place while navigating (a charge
    /// into a wall or a monster, a skill that never fires, a waypoint that can't be reached). Each stage
    /// happens once per episode and a bit of progress resets the lot:
    /// 1. log what the situation is - health, nearby monsters, keys held - and release stuck keys;
    /// 2. stop using movement skills and just walk;
    /// 3. step off in a direction that isn't the blocked one, and replan from there;
    /// 4. give up, rather than stand still until something kills the character.
    /// Returns true when it took over the tick.</summary>
    private bool HandleStuck(Entity player, PathStep step, NavTarget target)
    {
        if (DateTime.Now < _sidestepUntil)
        {
            Move(_sidestepWorld, _sidestepGrid, $"{target.Label} - stepping clear of an obstacle");
            return true;
        }

        var seconds = _stuck.Observe(player.PosNum);

        if (_stuck.Reached(seconds, StuckDiagnoseSeconds, 1))
        {
            var released = _followManager.ReleaseStuckSkillKeys();
            _followManager.LogDebug($"STUCK for {seconds:F1}s at grid ({player.GridPosNum.X:F0},{player.GridPosNum.Y:F0}), " +
                                    $"step {step.Kind} toward grid ({step.Grid.X:F0},{step.Grid.Y:F0}); " +
                                    $"{_followManager.DescribeSurroundings()}; stuck skill keys released: {released}");

            // Pinned beside a closed door is a door in the way, whether or not the route passes through
            // it: open the nearest one in reach rather than walking at it.
            if (_followManager.TryOpenDoorOnRoute(null, null, anyDoorWithin: 250f))
            {
                _stuck.Reset();
                return true;
            }
        }

        if (_stuck.Reached(seconds, StuckWalkOnlySeconds, 2))
        {
            _followManager.SuppressMovementSkills(6000);
            _followManager.LogDebug($"STUCK for {seconds:F1}s - movement skills off for 6 s, walking only");
        }

        if (_stuck.Reached(seconds, StuckSidestepSeconds, 3))
        {
            var blocked = new Vector2(step.World.X - player.PosNum.X, step.World.Y - player.PosNum.Y);
            if (_followManager.TryPickSidestep(blocked, out var world, out var grid))
            {
                _sidestepWorld = world;
                _sidestepGrid = grid;
                _sidestepUntil = DateTime.Now.AddMilliseconds(1500);
                _pathFollower.Reset();
                _followManager.LogDebug($"STUCK for {seconds:F1}s - stepping aside to grid ({grid.X:F0},{grid.Y:F0}) and replanning");
                Move(world, grid, $"{target.Label} - stepping clear of an obstacle");
                return true;
            }

            _pathFollower.Reset();
            _followManager.LogDebug($"STUCK for {seconds:F1}s - no open ground to step aside to, replanning");
        }

        if (_stuck.Reached(seconds, StuckGiveUpSeconds, 4))
        {
            _followManager.StopMovement();
            _followManager.LogDebug($"STUCK for {seconds:F1}s - giving up: {_followManager.DescribeSurroundings()}");
            Stop($"stuck - no progress for {seconds:F0} s at grid ({player.GridPosNum.X:F0},{player.GridPosNum.Y:F0})");
            return true;
        }

        return false;
    }

    /// <summary>Runs one tick of navigation. Returns true whenever a destination is set - navigation
    /// owns the tick then, whether or not it could issue an input this frame - and false only when
    /// there is nothing to navigate to.</summary>
    public bool Update()
    {
        if (ActiveTarget is not { } target) return false;

        var player = _gameController.Player;
        if (player == null || !player.IsAlive)
        {
            _followManager.StopMovement();
            Stop("stopped - character is dead or unavailable");
            return true;
        }

        if (DateTime.Now > _deadline)
        {
            _followManager.StopMovement();
            Stop("gave up - timed out");
            return true;
        }

        var settings = FollowHer.Instance.Settings;
        var targetGrid = PathFollower.RoundGrid(target.GridPos);
        var targetWorld = _pathFollower.GridToWorld(targetGrid.x, targetGrid.y);
        var distance = Vector3.Distance(player.PosNum, targetWorld);

        if (distance <= settings.Navigation.ArrivalDistance.Value)
        {
            _followManager.StopMovement();
            Stop($"arrived at {target.Label}");
            return true;
        }

        var step = _pathFollower.GetNextStep(targetGrid, settings.Movement.KeepWithinDistance.Value * 1.5f);

        // A closed door on the route is clicked open before walking on; standing to click it is not
        // being stuck.
        if (step.Kind is PathStepKind.Moving or PathStepKind.Arrived &&
            _followManager.TryOpenDoorOnRoute(_pathFollower.UpcomingWaypoints(8), _pathFollower.FirstBlockedCellOnRoute(8)))
        {
            _stuck.Reset();
            Status = $"{target.Label} - {distance:F0} away, opening a door";
            return true;
        }

        // Only ticks that are meant to be moving count towards "stuck": waiting at a jump takeoff or
        // for terrain data is standing still on purpose.
        if (step.Kind is PathStepKind.Moving or PathStepKind.Direct or PathStepKind.Arrived)
        {
            if (HandleStuck(player, step, target)) return true;
        }
        else
        {
            _stuck.Reset();
        }

        switch (step.Kind)
        {
            case PathStepKind.Moving:
                _consecutiveNoPath = 0;
                Move(step.World, step.Grid,
                    $"{target.Label} - {distance:F0} away, {_pathFollower.RemainingWaypoints} waypoint(s) left");
                return true;

            case PathStepKind.Jump:
                _consecutiveNoPath = 0;
                Status = _followManager.ExecuteJump(step, _pathFollower)
                    ? $"{target.Label} - {distance:F0} away, jumping an obstacle"
                    : $"{target.Label} - {distance:F0} away, waiting at a jump for a skill to be ready";
                return true;

            case PathStepKind.Direct:
                _consecutiveNoPath = 0;
                Move(step.World, step.Grid, $"{target.Label} - {distance:F0} away, walking straight (pathfinding is off)");
                return true;

            case PathStepKind.NoTerrainData:
                // Setting off in a straight line at a target across the zone just walks into the
                // first wall. The terrain read retries on its own, so hold until it lands.
                _followManager.StopMovement();
                Status = $"{target.Label} - waiting for this area's terrain data";
                return true;

            case PathStepKind.Arrived:
                // The waypoint tolerance is shared with following, where it's sized for chasing a
                // moving leader (KeepWithinDistance) - so the path can run out while still short of
                // a fixed target. Close the remaining gap directly instead of reporting an arrival
                // we haven't made; the distance check above is what actually declares success.
                _consecutiveNoPath = 0;
                Move(targetWorld, new Vector2(targetGrid.x, targetGrid.y), $"{target.Label} - {distance:F0} away, final approach");
                return true;

            default:
                // Only count ticks where A* actually ran. Path computation is throttled, so counting
                // every tick would blow through the retry budget in a fraction of a second.
                if (step.PathJustComputed) _consecutiveNoPath++;
                if (_consecutiveNoPath >= MaxConsecutiveNoPath)
                {
                    _followManager.StopMovement();
                    // Distance alone can't cause this any more (a search that runs out of budget
                    // returns a best-effort route instead) - reaching here means every walkable cell
                    // reachable from here was explored and none of them was the target.
                    Stop($"{target.Label} is cut off - no walkable route to it from here");
                    return true;
                }

                Status = $"{target.Label} - no path found, retrying ({_consecutiveNoPath}/{MaxConsecutiveNoPath})";
                _followManager.StopMovement();
                return true;
        }
    }
}
