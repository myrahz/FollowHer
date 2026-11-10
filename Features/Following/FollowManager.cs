using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using FollowHer.Core.Combat.Skills;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using FollowHer.Features.Following.Pathfinding;
using FollowHer.Features.Following.Tasks;
using FollowHer.Features.Navigation;
using FollowHer.Settings;
using FollowHer.Utils;

namespace FollowHer.Features.Following;

public class FollowManager
{
    private const string MoveSkillName = "Move";
    private const float FollowPortalWalkTolerance = 30f;
    private const int MaxFollowPortalAttempts = 10;
    private const int PortalClickCooldownMs = 2500;
    private const int TpConfirmationTimeoutMs = 1500;
    // 1 grid unit = 250/23 world units (confirmed via ExileCore's own GridToWorld conversion,
    // matching the constant already used by other plugins in this install, e.g. Radar.cs).
    private const float GridToWorldMultiplier = 250f / 23f;
    private const int PursuitModeSwitchCooldownMs = 500;

    private enum PursuitMode { Direct, Pathfind }

    private readonly GameController _gameController;
    private readonly SkillHandler _skillHandler;
    private readonly SkillMonitor _skillMonitor;
    private readonly LineOfSight _lineOfSight;
    private readonly PathFollower _pathFollower;
    private readonly Random _random = new();

    /// <summary>Manual "walk to this Radar target" navigation. Owned here because it drives the same
    /// movement primitives (ExecuteMovement/StopMovement) and takes priority over following while a
    /// destination is set.</summary>
    public NavigationManager Navigation { get; }

    // Updated whenever the leader entity is visible - fallback target once they're no longer
    // directly resolvable (still believed to be in the same zone).
    private (int x, int y)? _lastKnownLeaderGrid;
    // Same idea in world coordinates - used as the proximity anchor for quest tasks so they can
    // still tell "the leader was near this quest" after the leader has moved off or left the zone.
    private Vector3? _lastKnownLeaderWorldPos;

    // The single most recent target A* successfully reached - used as the search anchor when
    // pathfinding subsequently fails (the leader likely just stepped through a transition).
    private (int x, int y)? _lastPathfindableLeaderGrid;
    private (int x, int y)? _portalSearchReference;
    private int _portalClickAttempts;
    private DateTime _nextPortalClickAt = DateTime.MinValue;

    // Rolling record of transitions the follower has clicked - (area hash, name, grid). Used as a
    // recovery hint: when lost (e.g. after dying and resurrecting) with no live transition in range,
    // head for one we've entered before in this same area. Not cleared on area change; entries are
    // matched to the current area by hash so stale cross-zone coords are never used.
    private const int MaxTransitionHistory = 5;
    private readonly List<(uint areaHash, string name, Vector2 grid)> _transitionHistory = new();

    private string _lastKnownLeaderZone = "";
    private DateTime _leaderZoneChangeTime = DateTime.MinValue;
    private DateTime _nextTpActionAt = DateTime.MinValue;
    private DateTime? _tpButtonClickedAt;

    private DateTime _nextMovementInputAt = DateTime.MinValue;
    private Vector3? _lastMoveTargetWorld;

    // Sticky direct-walk-vs-pathfind decision - a single noisy LOS raycast right at a
    // corner/doorway edge shouldn't be able to flip the movement target every tick.
    private PursuitMode? _pursuitMode;
    private DateTime _pursuitModeLockedUntil = DateTime.MinValue;

    // Side-tasks checked (in order) before any follow/movement decision each tick - add new
    // IFollowTask implementations here to extend what Follow can do besides move/attack.
    private readonly List<IFollowTask> _tasks = new()
    {
        new QuestItemPickupTask(),
        new QuestObjectClickTask(),
    };

    public FollowManager(GameController gameController, SkillHandler skillHandler, SkillMonitor skillMonitor)
    {
        _gameController = gameController;
        _skillHandler = skillHandler;
        _skillMonitor = skillMonitor;
        _lineOfSight = new LineOfSight(gameController);
        _pathFollower = new PathFollower(gameController, _lineOfSight, PathingMode.Chase, LongestUsableJumpWorld);

        // Its own PathFollower, so switching between following and manual navigation never inherits
        // the other's cached route.
        Navigation = new NavigationManager(gameController, this,
            new PathFollower(gameController, _lineOfSight, PathingMode.Fixed, LongestUsableJumpWorld));
        FollowHer.Instance.Navigation = Navigation;

        EventBus.Instance.Subscribe<RenderEvent>(HandleRender);
    }

    public bool Update()
    {
        var settings = FollowHer.Instance.Settings.Movement;

        // Navigator mode owns movement entirely: it walks to its destination and does nothing else -
        // in particular it never falls through to following a leader, whatever the leader settings say.
        if (FollowHer.Instance.CurrentMode == PluginMode.Navigator)
        {
            try
            {
                if (Navigation.Update()) return true;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[FollowManager] Error during navigation: {ex.Message}");
                Navigation.Stop($"error: {ex.Message}");
            }

            return NotFollowing("navigator mode - no destination");
        }

        // Gate on the live movement state (the hotkey-controlled toggle), not the raw Enable
        // setting - otherwise toggling movement on via the hotkey while Enable is unchecked would
        // let the routine run but never actually follow. MovementEnabled is set earlier this tick,
        // before the routine's TickEvent is dispatched, so it's current here.
        if (!FollowHer.Instance.MovementEnabled) return NotFollowing("movement is toggled off");

        var leaderName = FollowHer.Instance.Settings.LeaderName.Value;
        if (string.IsNullOrWhiteSpace(leaderName)) return NotFollowing("no leader name configured");

        var player = _gameController.Player;
        if (player == null) return NotFollowing("no player entity");
        if (!player.IsAlive) return NotFollowing("player reads as not alive");

        try
        {
            // The teleport-to-player confirmation window is modal: if it's open, click it and
            // nothing else this tick.
            if (TryConfirmOpenTeleportWindow()) return true;

            // Once the TP button has been clicked, the confirmation window may take a moment to
            // appear. Until it does (or the wait times out), freeze every other action - otherwise
            // the follow logic keeps running and clicks the zone transition in that gap, which is
            // exactly the "clicked teleport, then clicked the transition before the window popped"
            // bug. The rule of thumb: while a teleport is in flight, click nothing but the window.
            if (IsTeleportInProgress())
            {
                StopMovement();
                return true;
            }

            var currentArea = _gameController.Area?.CurrentArea;
            // Disabling movement in town only blocks normal same-zone following - it must not
            // block the leader-left-town case, so the zone-transition check below still runs.
            var blockMovementInTown = settings.DisableMovementInTown &&
                                       (currentArea?.IsTown == true || currentArea?.IsHideout == true);

            var leaderEntity = LeaderLocator.FindLeaderEntity(_gameController, leaderName);

            if (!blockMovementInTown)
            {
                var taskContext = new FollowTaskContext(_gameController, player, this, leaderEntity,
                    leaderEntity?.PosNum ?? _lastKnownLeaderWorldPos);
                foreach (var task in _tasks)
                {
                    if (task.TryExecute(taskContext))
                    {
                        return true;
                    }
                }
            }

            // Zone check first: if the party panel confirms the leader is in a different zone,
            // that fully owns the decision - direct/pathfinding movement only applies same-zone.
            var leaderPartyMember = PartyPanelScanner.GetLeaderPartyMember(_gameController, leaderName);
            var currentZone = currentArea?.DisplayName;

            if (leaderPartyMember != null && !string.Equals(leaderPartyMember.ZoneName, currentZone, StringComparison.Ordinal))
            {
                LogDebug($"Leader reported in different zone '{leaderPartyMember.ZoneName}' (current '{currentZone}') - attempting transition");
                return TryFollowThroughZoneTransition(leaderPartyMember, currentZone);
            }

            if (blockMovementInTown)
            {
                // Leader is confirmed in this same town/hideout - movement is disabled here.
                StopMovement();
                return true;
            }

            (int x, int y)? targetGrid;

            if (leaderEntity != null)
            {
                var newGrid = RoundGrid(leaderEntity.GridPosNum);

                // Detect a sudden large jump in the leader's position: you're right next to them and
                // in one update they read from across the map. That's not real movement - they went
                // through a transition/teleport and the far reading is stale. Treat it as a
                // disappearance at their PREVIOUS position (where the transition is) instead of
                // chasing the jumped-to spot. _lastKnownLeaderGrid is reset on area change, so this
                // can't misfire on the first tick after a zone load.
                var jumpThreshold = settings.LeaderTeleportJumpDistance.Value;
                if (jumpThreshold > 0 && _lastKnownLeaderGrid.HasValue)
                {
                    // Nearby takes priority over the jump heuristic: if the leader is right next to
                    // us now, they're clearly here - a big prev->new delta is a stale/edge read
                    // snapping back, NOT a transition. Only consider it a jump when the leader also
                    // ended up far from us (they actually went somewhere).
                    var currentDistanceToLeader = Vector3.Distance(player.PosNum, leaderEntity.PosNum);
                    if (currentDistanceToLeader > jumpThreshold)
                    {
                        var prevGrid = _lastKnownLeaderGrid.Value;
                        var jumpWorld = Vector2.Distance(
                            new Vector2(prevGrid.x, prevGrid.y),
                            new Vector2(newGrid.x, newGrid.y)) * GridToWorldMultiplier;

                        // ...and only if we also can't actually path to where they now read. If a
                        // walkable route to the far position exists, they're reachable - just follow
                        // it. So this fires only when the leader is far AND jumped AND unreachable.
                        if (jumpWorld > jumpThreshold && !HasWalkablePath(_pathFollower.StandingCell(), newGrid))
                        {
                            LogDebug($"Leader position jumped {jumpWorld:F0} (> {jumpThreshold}), now {currentDistanceToLeader:F0} away and unreachable - treating as a transition at their previous position");
                            // Keep _lastKnownLeaderGrid at the pre-jump spot (don't adopt the far
                            // reading) so we stay committed to finding the transition there; the
                            // entity usually goes null a tick or two later and normal
                            // transition-following takes over from the same anchor.
                            _lastPathfindableLeaderGrid = prevGrid;
                            return TryFollowThroughRecentPortal(player.PosNum, prevGrid);
                        }
                    }
                }

                targetGrid = newGrid;
                _lastKnownLeaderGrid = targetGrid;
                _lastKnownLeaderWorldPos = leaderEntity.PosNum;
                _lastKnownLeaderZone = "";
                _leaderZoneChangeTime = DateTime.MinValue;
            }
            else
            {
                targetGrid = _lastKnownLeaderGrid;
            }

            if (targetGrid == null)
            {
                LogDebug("Leader not visible and never seen this zone - nothing to do yet");
                StopMovement();
                return true;
            }

            if (leaderEntity != null)
            {
                var distanceToLeader = Vector3.Distance(player.PosNum, leaderEntity.PosNum);
                var shouldMove = distanceToLeader >= settings.TransitionDistance ||
                                  (distanceToLeader >= settings.KeepWithinDistance && settings.CloseFollow);

                LogDebug($"Leader visible, distance={distanceToLeader:F0}, shouldMove={shouldMove}");

                var hasLineOfSight = HasClearLineOfSight(player.GridPosNum, leaderEntity.GridPosNum);

                // Record the leader's position as the last reachable one whenever there's a clear
                // walkable line to them - this is the anchor the transition-following fallback
                // searches around. It must be updated during direct (LOS) following too, not only
                // in the pathfinding branch below: a whole zone spent following in a straight line
                // would otherwise leave this null, and stepping through a same-zone transition
                // would never be handled (the fallback can't find a transition it has no anchor for).
                if (hasLineOfSight)
                {
                    _lastPathfindableLeaderGrid = RoundGrid(leaderEntity.GridPosNum);
                }

                if (!shouldMove)
                {
                    StopMovement();
                    return true;
                }

                if (DeterminePursuitMode(hasLineOfSight) == PursuitMode.Direct)
                {
                    return ExecuteMovement(leaderEntity.PosNum, leaderEntity.GridPosNum);
                }

                // LOS to the leader is blocked, so we'd normally take the walkable detour. But if a
                // blink skill can punch straight through the obstacle and the detour is much longer
                // than the straight-line hop, jump directly to them instead of pathfinding around.
                var dashShortcut = TryGetDirectDashShortcut(player, leaderEntity, settings);
                if (dashShortcut != null)
                {
                    LogDebug($"Dashing directly to leader with '{dashShortcut.Name}' instead of pathfinding a longer route");
                    return ExecuteMovement(leaderEntity.PosNum, leaderEntity.GridPosNum, dashShortcut);
                }

                LogDebug("Leader visible but line of sight is blocked - pathfinding instead of walking straight at them");
            }

            return TryFollowPathToTarget(player, targetGrid.Value, settings, leaderEntity != null);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[FollowManager] Error during update: {ex.Message}");
            return false;
        }
    }

    private string _lastNotFollowingReason;

    // Every one of these means "no destination this tick", which the caller answers by releasing the
    // movement key. That used to be a blind, held Move press, so the reason it happened never had to
    // be visible; now that it's a real decision, log it - once per change, since it repeats per tick.
    private bool NotFollowing(string reason)
    {
        if (reason != _lastNotFollowingReason)
        {
            _lastNotFollowingReason = reason;
            LogDebug($"Not following: {reason}");
        }

        return false;
    }

    private static (int x, int y) RoundGrid(Vector2 gridPos) => PathFollower.RoundGrid(gridPos);

    private bool HasClearLineOfSight(Vector2 playerGrid, Vector2 targetGrid)
    {
        try
        {
            return _lineOfSight.HasLineOfSight(playerGrid, targetGrid, LineOfSightDataType.Walkable);
        }
        catch
        {
            return false;
        }
    }

    // Blink/teleport movement skills pass OVER obstacles (gaps, chasms, low terrain) but not over
    // anything solid. On the terrain layer only the value 5 can be flown over (the Jumpable line of
    // sight), as opposed to the Walkable layer used by HasClearLineOfSight (which blocks on anything
    // you can't walk on, e.g. a gap you could still blink across). A blink must only be used when
    // this is clear.
    private bool HasTargetingLineOfSight(Vector2 playerGrid, Vector2 targetGrid)
    {
        try
        {
            return _lineOfSight.HasLineOfSight(playerGrid, targetGrid, LineOfSightDataType.Jumpable);
        }
        catch
        {
            return false;
        }
    }

    // Only switches direct-walk <-> pathfind once the cooldown has elapsed, so a single noisy
    // LOS reading right at a corner/doorway edge can't flip the movement target every tick.
    private PursuitMode DeterminePursuitMode(bool hasClearLineOfSight)
    {
        var desiredMode = hasClearLineOfSight ? PursuitMode.Direct : PursuitMode.Pathfind;

        if (_pursuitMode == null || (desiredMode != _pursuitMode.Value && DateTime.Now >= _pursuitModeLockedUntil))
        {
            _pursuitMode = desiredMode;
            _pursuitModeLockedUntil = DateTime.Now.AddMilliseconds(PursuitModeSwitchCooldownMs);
        }

        return _pursuitMode.Value;
    }

    // Whether A* can find any walkable route from start to target. Used by the leader-jump check to
    // avoid treating a big position jump as a transition when the leader is actually still
    // reachable. Only called in that (rare, leader-is-far) path, so the extra A* isn't per-tick.
    private bool HasWalkablePath((int x, int y) start, (int x, int y) target) =>
        _pathFollower.HasWalkablePath(start, target);

    // Whether there's a walkable route from the player to a target grid cell. Exposed for quest
    // tasks: once they can see a quest and confirm a path, they just click it and let the game walk
    // there, rather than issuing their own movement (which fought the game's click-to-move).
    internal bool HasWalkablePathTo(Vector2 targetGridPos)
    {
        if (_gameController.Player == null) return false;
        return HasWalkablePath(_pathFollower.StandingCell(), RoundGrid(targetGridPos));
    }

    private bool TryFollowPathToTarget(Entity player, (int x, int y) target, MovementSettings settings, bool leaderVisible)
    {
        var start = RoundGrid(player.GridPosNum);
        if (start == target)
        {
            if (!leaderVisible)
            {
                // Arrived at the leader's last known position but they're nowhere to be found -
                // they most likely stepped through a transition right here (the party panel still
                // reports the same zone, so this isn't a real zone change) - look for it instead
                // of idling here forever.
                _lastPathfindableLeaderGrid = target;
                return TryFollowThroughRecentPortal(player.PosNum, target);
            }

            StopMovement();
            return true;
        }

        // Route computation/caching/smoothing lives in PathFollower, shared with the manual target
        // navigator (Features/Navigation) so both drive identical pathfinding. Everything below is
        // just the leader-specific interpretation of the outcome.
        var step = _pathFollower.GetNextStep(target, settings.KeepWithinDistance * 1.5f);

        if (step.PathJustComputed)
        {
            if (step.Kind == PathStepKind.NoPath)
            {
                LogDebug("No path found to leader's position - they may have just gone through a transition");
            }
            else
            {
                _lastPathfindableLeaderGrid = target;
                LogDebug($"Computed path to leader with {_pathFollower.WaypointCount} waypoint(s)");
            }
        }

        switch (step.Kind)
        {
            case PathStepKind.Moving:
                return ExecuteMovement(step.World, step.Grid);

            case PathStepKind.Jump:
                ExecuteJump(step, _pathFollower);
                return true;

            // Pathfinding disabled, or the area's terrain isn't readable yet - walk straight at the
            // leader. Unchanged from before terrain availability became its own outcome: for
            // following, heading roughly toward a leader we can see beats standing still.
            case PathStepKind.Direct:
            case PathStepKind.NoTerrainData:
                return ExecuteMovement(step.World, step.Grid);

            case PathStepKind.NoPath when leaderVisible:
                // The leader is still visible - A* just couldn't reach their exact cell (they're
                // often standing on a cell it treats as blocked). They haven't gone anywhere, so
                // DON'T hunt for a transition; walk straight toward them instead.
                return ExecuteMovement(GridToWorldPosition(target.x, target.y), new Vector2(target.x, target.y));

            case PathStepKind.NoPath:
                // No path AND the leader is out of sight - they've genuinely left (through a
                // transition right here). This is the "disappeared" case: go find it.
                return TryFollowThroughRecentPortal(player.PosNum, target);

            case PathStepKind.Arrived when !leaderVisible:
                // Reached the end of the path to the leader's last known position and they're still
                // not visible, so look for a nearby transition instead of idling.
                return TryFollowThroughRecentPortal(player.PosNum, target);

            default:
                StopMovement();
                return true;
        }
    }

    // Pathfinding to the leader's believed position just failed while we still think we're in the
    // same zone - most likely they stepped through a transition the party panel hasn't caught up
    // to reporting yet. Look for a portal near the last position we could actually reach, rather
    // than near the player's own position.
    private bool TryFollowThroughRecentPortal(Vector3 playerPos, (int x, int y) fallbackTarget)
    {
        // The leader vanished AT the transition, so their last-known grid is the best place to
        // center the search - better than the last-*reachable* grid, which can fall short of the
        // transition if the leader rounded a corner before going through it.
        var searchGrid = fallbackTarget;
        var searchWorld = GridToWorldPosition(searchGrid.x, searchGrid.y);

        if (_portalSearchReference != searchGrid)
        {
            _portalSearchReference = searchGrid;
            _portalClickAttempts = 0;
            _nextPortalClickAt = DateTime.MinValue;
        }

        if (_portalClickAttempts >= MaxFollowPortalAttempts)
        {
            LogDebug("Gave up looking for a transition near where the leader was last seen");
            StopMovement();
            return true;
        }

        // Look immediately (not only after walking to an anchor), and from several centers -
        // where the leader vanished, then the last reachable spot, then our own position -
        // so a transition already in render range is picked up right away.
        var searchRadius = FollowHer.Instance.Settings.Movement.TransitionSearchRadius.Value;
        var transition = FindNearbyTransition(searchWorld, searchRadius)
            ?? (_lastPathfindableLeaderGrid is { } anchor
                    ? FindNearbyTransition(GridToWorldPosition(anchor.x, anchor.y), searchRadius)
                    : null)
            ?? FindNearbyTransition(playerPos, searchRadius);

        if (transition == null)
        {
            // Nothing in range yet - close the distance toward where the leader vanished so a
            // transition comes into render range, then look again next tick.
            if (Vector3.Distance(playerPos, searchWorld) > FollowPortalWalkTolerance)
            {
                return ExecuteMovement(searchWorld, new Vector2(searchGrid.x, searchGrid.y));
            }

            // Recovery hint: we're at the leader's last spot with no transition in sight (e.g. we
            // died, resurrected at the checkpoint, and the leader has moved on). Head for the
            // nearest transition we've actually entered before in this same area - once we're near
            // it, the live transition comes into range above and gets clicked.
            var remembered = NearestRememberedTransition(playerPos);
            if (remembered is { } rememberedGrid)
            {
                var rememberedWorld = GridToWorldPosition(rememberedGrid.x, rememberedGrid.y);
                if (Vector3.Distance(playerPos, rememberedWorld) > FollowPortalWalkTolerance)
                {
                    LogDebug("No transition here - heading to one entered earlier in this area");
                    return ExecuteMovement(rememberedWorld, new Vector2(rememberedGrid.x, rememberedGrid.y));
                }
            }

            LogDebug("At the leader's last position but no transition found in range");
            StopMovement();
            return true;
        }

        var target = transition.Value;
        var distanceToPortal = Vector3.Distance(playerPos, target.WorldPos);
        var approachDistance = FollowHer.Instance.Settings.Movement.PortalApproachDistance.Value;
        if (distanceToPortal > approachDistance)
        {
            return ExecuteMovement(target.WorldPos, target.GridPos);
        }

        if (DateTime.Now < _nextPortalClickAt) return true;

        LogDebug("Found a transition near where the leader was last seen - clicking it");
        // Release any held movement input first - a still-held Move key/mouse button can swallow
        // or override the click that follows.
        StopMovement();
        ClickTransition(target);
        _portalClickAttempts++;
        _nextPortalClickAt = DateTime.Now.AddMilliseconds(PortalClickCooldownMs);
        return true;
    }

    private bool TryFollowThroughZoneTransition(PartyMemberInfo leaderPartyMember, string currentZone)
    {
        if (!string.Equals(_lastKnownLeaderZone, leaderPartyMember.ZoneName, StringComparison.Ordinal))
        {
            _lastKnownLeaderZone = leaderPartyMember.ZoneName;
            _leaderZoneChangeTime = DateTime.Now;
            _tpButtonClickedAt = null;
        }

        if (!IsLeaderZoneInfoReliable(leaderPartyMember, currentZone))
        {
            // Zone text just changed and may still be mid-update - wait for it to settle
            // before acting on it, rather than chasing a stale/half-written zone name.
            return true;
        }

        var referencePosition = _lastPathfindableLeaderGrid.HasValue
            ? GridToWorldPosition(_lastPathfindableLeaderGrid.Value.x, _lastPathfindableLeaderGrid.Value.y)
            : _gameController.Player.PosNum;

        // (A teleport already in flight is handled globally at the top of Update, which returns
        // before reaching here - so this only runs when no teleport is pending.)

        // Prefer walking to and clicking the matching zone transition over teleporting - only
        // teleport if it's too far to bother walking to, or we can't see one at all.
        var playerPos = _gameController.Player.PosNum;
        var transition = GetBestTransition(leaderPartyMember.ZoneName, referencePosition);
        var maxWalkDistance = FollowHer.Instance.Settings.Movement.ZoneTransitionMaxWalkDistance.Value;

        // A transition isn't necessarily the SAME connection the leader actually used - a zone can
        // have multiple distinct transitions to another zone (e.g. we die and respawn at the zone's
        // entrance, which has its own transition, while the leader went through a different one).
        // Only trust the walk path when the candidate is close to BOTH where we are now AND where
        // the leader was last confirmed - if we just respawned elsewhere, the nearby-to-us candidate
        // fails this second check and we correctly fall back to teleporting straight to the leader.
        var transitionNearReference = transition != null &&
                                       Vector3.Distance(referencePosition, transition.Value.WorldPos) <= maxWalkDistance;

        if (transition != null && transitionNearReference &&
            Vector3.Distance(playerPos, transition.Value.WorldPos) <= maxWalkDistance)
        {
            return TryWalkToZoneTransitionPortal(transition.Value, playerPos);
        }

        LogDebug(transition == null
            ? "No zone transition visible - falling back to teleport"
            : "Zone transition is too far from where we are or from the leader's last known position - falling back to teleport");
        return TryTeleportToLeader(leaderPartyMember, referencePosition);
    }

    private bool TryWalkToZoneTransitionPortal(TransitionTarget transition, Vector3 playerPos)
    {
        var distanceToPortal = Vector3.Distance(playerPos, transition.WorldPos);
        var approachDistance = FollowHer.Instance.Settings.Movement.PortalApproachDistance.Value;
        if (distanceToPortal > approachDistance)
        {
            return ExecuteMovement(transition.WorldPos, transition.GridPos);
        }

        if (DateTime.Now < _nextPortalClickAt) return true;

        LogDebug("Walking to and clicking the zone transition");
        // Release any held movement input first - a still-held Move key/mouse button can swallow
        // or override the click that follows.
        StopMovement();
        ClickTransition(transition);
        _nextPortalClickAt = DateTime.Now.AddMilliseconds(PortalClickCooldownMs);
        return true;
    }

    private bool IsLeaderZoneInfoReliable(PartyMemberInfo leaderPartyMember, string currentZone)
    {
        var zoneName = leaderPartyMember.ZoneName;
        if (string.IsNullOrEmpty(zoneName) || string.Equals(zoneName, currentZone, StringComparison.Ordinal))
            return false;

        var timeSinceChange = DateTime.Now - _leaderZoneChangeTime;
        var bufferMs = FollowHer.Instance.Settings.Movement.ZoneUpdateBuffer.Value;
        return timeSinceChange >= TimeSpan.FromMilliseconds(bufferMs);
    }

    private IEnumerable<LabelOnGround> GetPortalCandidates()
    {
        return _gameController.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?
            .Where(x => x != null && x.IsVisible && x.Label is { IsValid: true, IsVisible: true } && x.ItemOnGround != null &&
                        x.ItemOnGround.Metadata != null &&
                        (x.ItemOnGround.Metadata.Contains("areatransition", StringComparison.OrdinalIgnoreCase) ||
                         x.ItemOnGround.Metadata.Contains("portal", StringComparison.OrdinalIgnoreCase)))
            ?? Enumerable.Empty<LabelOnGround>();
    }

    // A place we can walk to and click to change area - either a portal or an area transition.
    // Sourced from both ground labels and the entity list, and may or may not carry a clickable
    // label (area-transition doorways frequently have no visible ground label from a distance, so
    // those are clicked at their world position instead).
    private readonly struct TransitionTarget
    {
        public readonly Vector3 WorldPos;
        public readonly Vector2 GridPos;
        public readonly Element Label;
        public readonly string Name;

        public TransitionTarget(Vector3 worldPos, Vector2 gridPos, Element label, string name)
        {
            WorldPos = worldPos;
            GridPos = gridPos;
            Label = label;
            Name = name;
        }
    }

    // Portals and town portals reliably produce a visible ground label; area-transition doorways
    // often don't until you're right on top of them - so those are read straight from the entity
    // list, which exists regardless of label visibility. Doors (intra-zone) are deliberately
    // excluded so the follower doesn't click random doorways as if they were zone changes.
    private static readonly EntityType[] TransitionEntityTypes =
        { EntityType.AreaTransition, EntityType.Portal, EntityType.TownPortal };

    private IEnumerable<Entity> GetTransitionEntities()
    {
        var byType = _gameController?.EntityListWrapper?.ValidEntitiesByType;
        if (byType == null) yield break;

        foreach (var type in TransitionEntityTypes)
        {
            if (!byType.TryGetValue(type, out var list) || list == null) continue;
            foreach (var entity in list)
            {
                if (entity is { IsValid: true }) yield return entity;
            }
        }
    }

    // Merges both sources into a single candidate list, de-duplicated by grid cell so a portal that
    // appears as both a ground label and an entity isn't offered twice. Label-sourced candidates
    // come first so a clickable label is preferred over a bare world-position click for the same spot.
    private IEnumerable<TransitionTarget> GetTransitionCandidates()
    {
        var seen = new HashSet<(int, int)>();

        foreach (var label in GetPortalCandidates())
        {
            var grid = RoundGrid(label.ItemOnGround.GridPosNum);
            if (seen.Add(grid))
                yield return new TransitionTarget(label.ItemOnGround.PosNum, label.ItemOnGround.GridPosNum, label.Label, label.Label.Text);
        }

        foreach (var entity in GetTransitionEntities())
        {
            var grid = RoundGrid(entity.GridPosNum);
            if (seen.Add(grid))
                yield return new TransitionTarget(entity.PosNum, entity.GridPosNum, null, entity.RenderName);
        }
    }

    private void ClickTransition(TransitionTarget target)
    {
        RecordEnteredTransition(target);

        if (target.Label != null)
            ClickElement(target.Label);
        else
            ClickWorldPosition(target.WorldPos);
    }

    // Remembers a transition we just clicked, keyed to the current area, most-recent first. Clicking
    // the same transition again just refreshes its recency rather than adding a duplicate.
    private void RecordEnteredTransition(TransitionTarget target)
    {
        var areaHash = _gameController.Game.CurrentAreaHash;
        _transitionHistory.RemoveAll(t => t.areaHash == areaHash && Vector2.Distance(t.grid, target.GridPos) < 5f);
        _transitionHistory.Insert(0, (areaHash, target.Name ?? "", target.GridPos));
        if (_transitionHistory.Count > MaxTransitionHistory)
            _transitionHistory.RemoveAt(_transitionHistory.Count - 1);
    }

    // Nearest transition we've clicked before in the CURRENT area (by grid), or null if none. Used
    // as a recovery hint when the follower is lost and no live transition is in range.
    private (int x, int y)? NearestRememberedTransition(Vector3 playerPos)
    {
        var areaHash = _gameController.Game.CurrentAreaHash;
        (int x, int y)? best = null;
        var bestDistance = float.MaxValue;

        foreach (var t in _transitionHistory)
        {
            if (t.areaHash != areaHash) continue;
            var grid = ((int)t.grid.X, (int)t.grid.Y);
            var world = GridToWorldPosition(grid.Item1, grid.Item2);
            var distance = Vector3.Distance(playerPos, world);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = grid;
            }
        }

        return best;
    }

    private TransitionTarget? GetBestTransition(string leaderZoneName, Vector3 referencePosition)
    {
        var currentArea = _gameController.Area?.CurrentArea;
        var isHideout = currentArea?.IsHideout ?? false;
        var realLevel = currentArea?.RealLevel ?? 0;

        // Campaign: prefer a ground label whose text names the destination zone - most reliable when
        // a zone has several transitions. But many transitions have generic labels ("Stairs",
        // "Cave", "Entrance") that never contain the zone name, so this is only a preference.
        if (!isHideout && realLevel < 68 && !string.IsNullOrEmpty(leaderZoneName))
        {
            var named = GetPortalCandidates()
                .Where(x => x.Label.Text != null && x.Label.Text.Contains(leaderZoneName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => Vector3.Distance(referencePosition, x.ItemOnGround.PosNum))
                .FirstOrDefault();
            if (named != null)
                return new TransitionTarget(named.ItemOnGround.PosNum, named.ItemOnGround.GridPosNum, named.Label, named.Label.Text);
        }

        // Otherwise (hideout/endgame, or a generically-named transition like "Stairs" that couldn't
        // be matched by zone name) fall back to the nearest transition of any kind - label or entity
        // - to where the leader was last seen. This is what makes generic transitions work at all.
        return FindNearbyTransition(referencePosition, float.MaxValue);
    }

    private TransitionTarget? FindNearbyTransition(Vector3 referencePosition, float maxDistance)
    {
        TransitionTarget? best = null;
        var bestDistance = float.MaxValue;

        foreach (var candidate in GetTransitionCandidates())
        {
            var distance = Vector3.Distance(referencePosition, candidate.WorldPos);
            if (distance < maxDistance && distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private Vector3 GridToWorldPosition(int x, int y) => _pathFollower.GridToWorld(x, y);

    // Default path is teleport-to-player: click the party panel's TP button, then confirm the
    // popup it opens. Only if that popup never shows up (TP unavailable/failed) do we fall back
    // to hunting for the zone-transition portal near the leader's last reachable position.
    // Confirms an open teleport-to-player window. Returns true if the window was open (handled or
    // waiting out the click cooldown), false if there was no window. Called at the top of Update so
    // the modal is always resolved before any competing movement/click decision runs.
    private bool TryConfirmOpenTeleportWindow()
    {
        var confirmationButton = GetTpConfirmationButton();
        if (confirmationButton == null) return false;

        if (DateTime.Now < _nextTpActionAt) return true;

        LogDebug("Teleport confirmation window open - confirming before anything else");
        StopMovement();
        ClickElement(confirmationButton);
        _tpButtonClickedAt = null;
        _nextTpActionAt = DateTime.Now.AddMilliseconds(500);
        return true;
    }

    // A teleport is "in flight" from the moment we click the TP button until either the
    // confirmation window is dealt with or the wait for it times out. While it's in flight, the
    // zone-transition logic must not switch back to walking to / clicking a transition.
    private bool IsTeleportInProgress()
    {
        if (GetTpConfirmationButton() != null) return true;
        return _tpButtonClickedAt.HasValue &&
               DateTime.Now - _tpButtonClickedAt.Value < TimeSpan.FromMilliseconds(TpConfirmationTimeoutMs);
    }

    // Confirming an open window, and waiting while one is pending, are both handled globally at the
    // top of Update - so this only ever runs when no teleport is in flight. Its jobs are just:
    // click the TP button, or (if a prior teleport timed out with no window) fall back to the
    // zone transition.
    private bool TryTeleportToLeader(PartyMemberInfo leaderPartyMember, Vector3 referencePosition)
    {
        if (DateTime.Now < _nextTpActionAt) return true;

        // Reaching here with a click still recorded means the confirmation window never appeared
        // within the timeout (the in-flight wait already elapsed at the top of Update) - give up on
        // the teleport and try the zone transition instead.
        if (_tpButtonClickedAt.HasValue)
        {
            _tpButtonClickedAt = null;
            LogDebug("Teleport-to-player produced no confirmation window - falling back to the zone transition portal");
            return TryClickZoneTransitionPortal(leaderPartyMember.ZoneName, referencePosition);
        }

        if (leaderPartyMember.TpButton == null)
        {
            return TryClickZoneTransitionPortal(leaderPartyMember.ZoneName, referencePosition);
        }

        LogDebug("Clicking teleport-to-player button");
        StopMovement();
        ClickElement(leaderPartyMember.TpButton);
        _tpButtonClickedAt = DateTime.Now;
        _nextTpActionAt = DateTime.Now.AddMilliseconds(500);
        return true;
    }

    private bool TryClickZoneTransitionPortal(string leaderZoneName, Vector3 referencePosition)
    {
        var transition = GetBestTransition(leaderZoneName, referencePosition);
        if (transition == null) return true;

        LogDebug($"Found transition for zone '{leaderZoneName}' - clicking");
        StopMovement();
        ClickTransition(transition.Value);
        _nextTpActionAt = DateTime.Now.AddMilliseconds(500);
        return true;
    }

    private Element GetTpConfirmationButton()
    {
        var popup = _gameController.IngameState?.IngameUi?.PopUpWindow;
        if (popup?.Children?.Count > 0 &&
            popup.Children[0]?.Children?.Count > 0 &&
            popup.Children[0].Children[0]?.Children?.Count > 3 &&
            popup.Children[0].Children[0].Children[0]?.Text == "Are you sure you want to teleport to this player's location?")
        {
            return popup.Children[0].Children[0].Children[3].Children[0];
        }

        return null;
    }

    internal void ClickElement(Element element)
    {
        if (element == null) return;

        var position = element.GetClientRect().ClickRandomNum(5, 3) +
                        _gameController.Window.GetWindowRectangle().TopLeft.ToVector2Num();

        ExileCore.Input.SetCursorPos(position);
        ExileCore.Input.Click(MouseButtons.Left);
    }

    // Same click mechanics as ClickElement, but for a raw world entity with no ground label -
    // WorldToScreen already returns an absolute screen position, so no window-rect offset is added.
    internal void ClickWorldPosition(Vector3 worldPosition)
    {
        var screenPos = _gameController.IngameState.Camera.WorldToScreen(worldPosition);
        if (screenPos == Vector2.Zero) return;

        ExileCore.Input.SetCursorPos(screenPos);
        ExileCore.Input.Click(MouseButtons.Left);
    }

    // Called whenever Follow decides no movement is needed this tick - releases any held
    // movement/dash key so the character actually stops instead of drifting toward a stale
    // cursor position (see plan notes on the KeepWithinDistance freeze/ping-pong bug).
    // Internal so quest tasks can stop and settle before a precise label click.
    internal void StopMovement()
    {
        _skillHandler.ReleaseAllSkills();
        _nextMovementInputAt = DateTime.MinValue;
    }

    // Whether the player character is currently moving. Quest tasks use this to hold a precise
    // click until the character has actually stopped - clicking a ground label mid-move misses,
    // because the label's screen position shifts as the character (and camera) move.
    internal bool IsPlayerMoving()
    {
        return _gameController.Player?.GetComponent<Actor>()?.isMoving ?? false;
    }

    internal bool ExecuteMovement(Vector3 targetWorldPosition, Vector2? targetGridPosition = null, ActiveSkill forcedSkill = null)
    {
        var screenPos = _gameController.IngameState.Camera.WorldToScreen(targetWorldPosition);
        if (screenPos == Vector2.Zero) return false;

        _lastMoveTargetWorld = targetWorldPosition;

        if (DateTime.Now < _nextMovementInputAt) return true;

        // A caller-chosen skill (e.g. the direct-dash shortcut) bypasses the normal per-target
        // skill selection - the decision to use it, and which one, has already been made.
        var movementSkill = forcedSkill;
        var aimWorld = targetWorldPosition;

        if (movementSkill == null && targetGridPosition.HasValue)
        {
            var choice = TryGetMovementSkill(_gameController.Player.GridPosNum, targetGridPosition.Value);
            if (choice is { } picked)
            {
                movementSkill = picked.Skill;

                // The skill only covers its hop, so aim that far ahead along the line to the target
                // rather than all the way at it.
                if (picked.AimFraction < 1f)
                {
                    var playerWorld = _gameController.Player.PosNum;
                    aimWorld = playerWorld + (targetWorldPosition - playerWorld) * picked.AimFraction;
                    var aimScreen = _gameController.IngameState.Camera.WorldToScreen(aimWorld);
                    if (aimScreen != Vector2.Zero) screenPos = aimScreen;
                }
            }
        }

        using (ExileCore.Input.InputManager.BlockUserMouseInput())
        {
            ExileCore.Input.InputManager.MoveMouse(screenPos);
            _skillHandler.UseMovementSkill(movementSkill?.Name ?? MoveSkillName, true);
        }

        if (movementSkill != null)
        {
            LogDebug($"Using movement skill '{movementSkill.Name}' toward {aimWorld}");
        }

        var inputFrequency = FollowHer.Instance.Settings.Movement.InputFrequency.Value;
        _nextMovementInputAt = DateTime.Now.AddMilliseconds(inputFrequency);

        return true;
    }

    // Movement skills come in two mechanically different flavors, and each setting now maps onto
    // exactly one of them (see ActiveSkill.TravelsThroughObstacles):
    //   - DashEnabled -> blink-type skills (Frostblink/FlameDash/LightningWarp/BlinkArrow), which
    //     pass OVER obstacles but still need targeting LOS to the destination (they can't go through
    //     a solid wall). Used to cross something unwalkable you can still see across (a gap), not as
    //     a plain speed boost on ground that's already clear.
    //   - PreferMovementSkillsForTravel -> ground-dash skills (Shield Charge/Whirling Blades),
    //     which collide with obstacles like a moving hitbox - a thin center-line LOS pass isn't
    //     enough to guarantee they won't clip a corner, so they additionally require a full
    //     corridor clearance check (GridPathfinder.HasCorridorClearance) across the configured
    //     hitbox margin, and are only used when the path is already clear - never to punch
    //     through something, since they physically can't.
    // When LOS to the leader is blocked, decide whether to blink straight to them instead of
    // taking the walkable detour. Returns the blink skill to use, or null to fall back to
    // pathfinding. Only evaluated when a blink skill is actually off cooldown, so the extra A*
    // done here to measure the detour is infrequent rather than every tick.
    // Skills (including movement skills) can't be used in town/hideout, so never pick one there.
    private bool IsInTownOrHideout()
    {
        var area = _gameController.Area?.CurrentArea;
        return area != null && (area.IsTown || area.IsHideout);
    }

    private ActiveSkill TryGetDirectDashShortcut(Entity player, Entity leaderEntity, MovementSettings settings)
    {
        if (!settings.DashEnabled) return null;
        if (IsInTownOrHideout()) return null;

        // Only a blink-type skill can shortcut through the obstacle blocking LOS; a ground dash
        // would just collide with it.
        var blinkSkill = FollowHer.Instance.Settings.Movement.MovementSkills.Content
            .FirstOrDefault(s => s.Enabled && s.Name != MoveSkillName && s.TravelsThroughObstacles &&
                                  _skillMonitor.CanUseSkill(s));
        if (blinkSkill == null) return null;

        var directWorld = Vector3.Distance(player.PosNum, leaderEntity.PosNum);
        if (directWorld < settings.MovementSkillMinDistance || directWorld > settings.MovementSkillMaxDistance)
            return null;

        // A blink can only go where it can see. Without this it would try to blink straight into a
        // wall whenever the walkable path was long - it passes over gaps, not through solid terrain.
        if (!HasTargetingLineOfSight(player.GridPosNum, leaderEntity.GridPosNum))
            return null;

        var grid = _lineOfSight.GetGridReference(LineOfSightDataType.Walkable);
        if (grid == null) return null;

        var start = _pathFollower.StandingCell();
        var target = RoundGrid(leaderEntity.GridPosNum);

        var directGrid = Vector2.Distance(new Vector2(start.x, start.y), new Vector2(target.x, target.y));
        if (directGrid < 1f) return null;

        var path = GridPathfinder.AStar(grid, start, target, PathFollower.MaxPathSearchNodes);

        // No walkable route at all, but targeting LOS to the leader is clear (checked above) - i.e.
        // a gap/chasm we can see across but not walk across. Blink is the only way there, so take it.
        if (path == null || path.Count == 0) return blinkSkill;

        var ratio = PathGridLength(path) / directGrid;
        if (ratio >= settings.DashShortcutPathRatio)
        {
            LogDebug($"Dash shortcut: walkable route {ratio:F1}x the straight-line distance (>= {settings.DashShortcutPathRatio:F1}) - blinking");
            return blinkSkill;
        }

        return null;
    }

    private static float PathGridLength(List<(int x, int y)> path)
    {
        float total = 0f;
        for (var i = 1; i < path.Count; i++)
        {
            total += Vector2.Distance(
                new Vector2(path[i - 1].x, path[i - 1].y),
                new Vector2(path[i].x, path[i].y));
        }
        return total;
    }

    // Movement skills the path planner may jump with right now: enabled, given a jump range, and off
    // cooldown. Ordered shortest range first so a short hop doesn't burn the longest-range skill.
    private List<ActiveSkill> UsableJumpSkills() =>
        FollowHer.Instance.Settings.Movement.MovementSkills.Content
            .Where(s => s.Enabled && s.Name != MoveSkillName && s.MaxJumpDistance.Value > 0 &&
                        _skillMonitor.CanUseSkill(s))
            .OrderBy(s => s.MaxJumpDistance.Value)
            .ToList();

    /// <summary>The longest jump (world units) any usable skill can make right now, 0 if none or in a
    /// town. This is what the path planner is told it may jump, so it never plans a jump nothing can
    /// make.</summary>
    private float LongestUsableJumpWorld()
    {
        if (IsInTownOrHideout()) return 0f;

        var skills = UsableJumpSkills();
        return skills.Count == 0 ? 0f : skills.Max(s => s.MaxJumpDistance.Value);
    }

    /// <summary>Takes a planned jump: aims the shortest-range usable skill that reaches the landing
    /// at it. If none is ready (cooldown, or the one that was planned with is down), holds still at
    /// the takeoff and waits - the jump was chosen over a long walk, so it's worth waiting out.
    /// Returns whether a skill was used.</summary>
    internal bool ExecuteJump(PathStep step, PathFollower follower)
    {
        var player = _gameController.Player;
        if (player == null) return false;

        var distance = Vector3.Distance(player.PosNum, step.World);
        var ready = UsableJumpSkills();
        var skill = ready.FirstOrDefault(s => s.MaxJumpDistance.Value >= distance);
        if (skill == null)
        {
            // Skills are ready but the landing is out of their reach - the character stopped a little
            // short of the takeoff. Waiting here would be forever (nothing makes the distance shrink,
            // and the give-up timer only starts after a cast), so step closer. Only when every skill is
            // genuinely on cooldown is waiting the right thing.
            if (ready.Count > 0)
            {
                LogDebug($"Jump: ready skills reach at most {ready.Max(s => s.MaxJumpDistance.Value)} but the landing is {distance:F0} away - closing in");
                return ExecuteMovement(step.World, step.Grid);
            }

            LogDebug($"Jump: every jump skill is on cooldown ({distance:F0} units to the landing) - waiting at the takeoff");
            StopMovement();
            return false;
        }

        if (!ExecuteMovement(step.World, step.Grid, skill)) return false;

        follower.ReportJumpAttempt();
        LogDebug($"Jump: '{skill.Name}' toward {step.Grid} ({distance:F0} units, skill reaches {skill.MaxJumpDistance.Value})");
        return true;
    }

    /// <summary>A movement skill chosen for a step, and how far along the line to the step's target to
    /// aim it: 1 = at the target, less = a point that far along (its hop distance).</summary>
    private readonly record struct MovementChoice(ActiveSkill Skill, float AimFraction);

    private MovementChoice? TryGetMovementSkill(Vector2 playerGrid, Vector2 targetGrid)
    {
        var settings = FollowHer.Instance.Settings.Movement;
        if (!settings.DashEnabled && !settings.PreferMovementSkillsForTravel) return null;
        if (IsInTownOrHideout()) return null;

        // Compared in world units (like every other distance setting in this plugin), not grid
        // units, so it's directly comparable to what you see in-game rather than a hidden scale.
        var worldDistance = Vector2.Distance(playerGrid, targetGrid) * GridToWorldMultiplier;
        var hasClearLineOfSight = HasClearLineOfSight(playerGrid, targetGrid);

        // Blink is for crossing something you can't walk through but CAN see across (a gap/chasm):
        // walkable LOS blocked, but targeting LOS clear. Requiring targeting LOS here stops it from
        // trying to blink into a wall/corner (walkable AND targeting both blocked).
        if (settings.DashEnabled && !hasClearLineOfSight && HasTargetingLineOfSight(playerGrid, targetGrid))
        {
            if (worldDistance < settings.MovementSkillMinDistance || worldDistance > settings.MovementSkillMaxDistance)
            {
                LogDebug($"Movement skill: blink distance {worldDistance:F0} outside [{settings.MovementSkillMinDistance},{settings.MovementSkillMaxDistance}] - skipped");
                return null;
            }

            var blinkSkill = FollowHer.Instance.Settings.Movement.MovementSkills.Content
                .FirstOrDefault(s => s.Enabled && s.Name != MoveSkillName && s.TravelsThroughObstacles &&
                                      _skillMonitor.CanUseSkill(s));
            if (blinkSkill != null) return new MovementChoice(blinkSkill, 1f);
            LogDebug("Movement skill: DashEnabled path (walkable blocked, target visible) found no enabled/off-cooldown blink-type skill");
        }

        if (settings.PreferMovementSkillsForTravel)
        {
            // Gate on the corridor-clearance check alone, not also on hasClearLineOfSight - the
            // two use different walkability rules (LineOfSight only blocks on grid value 4;
            // GridPathfinder/HasCorridorClearance also blocks on 5, "Special" tiles), so requiring
            // both could silently reject a corridor the clearance check would otherwise approve.
            var grid = _lineOfSight.GetGridReference(LineOfSightDataType.Walkable);
            var candidates = FollowHer.Instance.Settings.Movement.MovementSkills.Content
                .Where(s => s.Enabled && s.Name != MoveSkillName && !s.TravelsThroughObstacles &&
                            _skillMonitor.CanUseSkill(s))
                .ToList();

            if (grid == null)
            {
                LogDebug("Movement skill: no terrain grid available yet - skipped");
            }
            else if (candidates.Count == 0)
            {
                LogDebug("Movement skill: no enabled/off-cooldown ground-dash skill found");
            }
            else
            {
                // Each skill is judged on its own terms - its hop and its clearance - and the first that
                // qualifies is used, so a skill with a roomy hitbox being blocked beside a wall doesn't
                // stop one that fits.
                foreach (var dashSkill in candidates)
                {
                    // A skill with a known hop only covers that much of the way to the target, so what
                    // has to be clear - and what the distance limits apply to - is the hop, not the
                    // whole distance to a waypoint that is usually several hops off. Without this a
                    // bend 300 units ahead vetoed a 100-unit leap.
                    var hop = dashSkill.EffectiveHopDistance;
                    var aimFraction = hop > 0 && worldDistance > hop ? hop / worldDistance : 1f;
                    var hopWorld = worldDistance * aimFraction;
                    var aimGrid = playerGrid + (targetGrid - playerGrid) * aimFraction;

                    var minDistance = Math.Max(settings.MovementSkillMinDistance, dashSkill.MinDistance.Value);
                    if (hopWorld < minDistance || hopWorld > settings.MovementSkillMaxDistance)
                    {
                        LogDebug($"Movement skill '{dashSkill.Name}': hop {hopWorld:F0} outside [{minDistance},{settings.MovementSkillMaxDistance}] - skipped");
                        continue;
                    }

                    // A per-skill hitbox radius means "too close to anything and it won't fire", so it
                    // checks a disc swept along the hop, start included. Without one, the global margin
                    // applies as before (three lines along the hop, endpoints skipped).
                    var radius = dashSkill.ClearanceRadius.Value;
                    string failureReason;
                    var clear = radius > 0f
                        ? GridPathfinder.HasCapsuleClearance(grid, playerGrid, aimGrid, radius, out failureReason)
                        : GridPathfinder.HasCorridorClearance(grid, playerGrid, aimGrid, settings.MovementSkillClearanceMargin, out failureReason);

                    if (!clear)
                    {
                        LogDebug($"Movement skill '{dashSkill.Name}': clearance check failed ({failureReason}); player grid ({playerGrid.X:F1},{playerGrid.Y:F1}) -> hop end ({aimGrid.X:F1},{aimGrid.Y:F1}), " +
                                 (radius > 0f ? $"hitbox radius={radius:F1}" : $"margin={settings.MovementSkillClearanceMargin}"));
                        continue;
                    }

                    return new MovementChoice(dashSkill, aimFraction);
                }
            }
        }

        return null;
    }

    private void HandleRender(RenderEvent evt)
    {
        var visual = FollowHer.Instance.Settings.Movement.Visual;
        if (!visual.ShowFollowPath || _lastMoveTargetWorld == null) return;

        var player = _gameController.Player;
        if (player == null) return;

        var playerScreen = _gameController.IngameState.Camera.WorldToScreen(player.PosNum);
        var targetScreen = _gameController.IngameState.Camera.WorldToScreen(_lastMoveTargetWorld.Value);
        if (playerScreen == Vector2.Zero || targetScreen == Vector2.Zero) return;

        evt.Graphics.DrawLine(playerScreen, targetScreen, visual.TaskLineWidth.Value, visual.TaskLineColor.Value);
    }

    internal void LogDebug(string message)
    {
        if (FollowHer.Instance.Settings.Movement.Debug.ShowDetailedDebug)
        {
            DebugWindow.LogMsg($"[Follow] {message}");
        }
    }

    public void Dispose()
    {
        EventBus.Instance.Unsubscribe<RenderEvent>(HandleRender);
        _lineOfSight.Dispose();
    }

    public void Reset(AreaInstance newArea)
    {
        // Radar's targets are per-area, so a destination picked in the zone we just left means
        // nothing here (its grid coordinates would point at an unrelated spot).
        Navigation.ResetForArea();
        ClearState();
    }

    public void Stop()
    {
        ClearState();
    }

    private void ClearState()
    {
        _lastKnownLeaderGrid = null;
        _lastKnownLeaderWorldPos = null;
        _pathFollower.Reset();
        _lastPathfindableLeaderGrid = null;
        _portalSearchReference = null;
        _portalClickAttempts = 0;
        _nextPortalClickAt = DateTime.MinValue;
        _lastKnownLeaderZone = "";
        _leaderZoneChangeTime = DateTime.MinValue;
        _nextTpActionAt = DateTime.MinValue;
        _tpButtonClickedAt = null;
        _nextMovementInputAt = DateTime.MinValue;
        _lastMoveTargetWorld = null;
        _pursuitMode = null;
        _pursuitModeLockedUntil = DateTime.MinValue;
        _lastNotFollowingReason = null;

        foreach (var task in _tasks)
        {
            task.Reset();
        }
    }
}
