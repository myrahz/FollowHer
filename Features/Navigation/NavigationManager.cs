using System;
using System.Numerics;
using ExileCore;
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
    // A* failing once is usually transient (the target cell is momentarily blocked, terrain data is
    // mid-refresh). Failing this many computations in a row means it's genuinely unreachable - report
    // that instead of quietly retrying until the timeout. Path computation is throttled to one every
    // 400ms, so this is roughly five seconds of trying.
    private const int MaxConsecutiveNoPath = 12;

    private readonly GameController _gameController;
    private readonly FollowManager _followManager;
    private readonly PathFollower _pathFollower;

    private DateTime _deadline = DateTime.MaxValue;
    private int _consecutiveNoPath;

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

        switch (step.Kind)
        {
            case PathStepKind.Moving:
                _consecutiveNoPath = 0;
                Move(step.World, step.Grid,
                    $"{target.Label} - {distance:F0} away, {_pathFollower.RemainingWaypoints} waypoint(s) left");
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
