using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Helpers;
using FollowHer.Core.Combat.Skills;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using FollowHer.Features.Following.Pathfinding;
using FollowHer.Features.Following.Tasks;
using FollowHer.Settings;
using FollowHer.Utils;

namespace FollowHer.Features.Following;

public class FollowManager
{
    private const string MoveSkillName = "Move";
    private const float FollowPortalWalkTolerance = 30f;
    private const float FollowPortalSearchRadius = 100f;
    private const int MaxFollowPortalAttempts = 10;
    private const int PortalClickCooldownMs = 2500;
    private const int TpConfirmationTimeoutMs = 1500;
    private const int MaxPathSearchNodes = 20000;
    private const int PathSmoothingLookahead = 6;
    private const float PathRetargetThresholdGrid = 20f;
    private const int MinPathRecomputeIntervalMs = 400;
    private const float DashMinGridDistance = 30f;
    private const float DashMaxGridDistance = 150f;
    private const int PursuitModeSwitchCooldownMs = 500;

    private enum PursuitMode { Direct, Pathfind }

    private readonly GameController _gameController;
    private readonly SkillHandler _skillHandler;
    private readonly SkillMonitor _skillMonitor;
    private readonly LineOfSight _lineOfSight;
    private readonly Random _random = new();

    // Updated whenever the leader entity is visible - fallback target once they're no longer
    // directly resolvable (still believed to be in the same zone).
    private (int x, int y)? _lastKnownLeaderGrid;

    private List<(int x, int y)> _currentPath;
    private int _currentPathIndex;
    private (int x, int y)? _currentPathTarget;
    private DateTime _nextPathRecomputeAt = DateTime.MinValue;

    // The single most recent target A* successfully reached - used as the search anchor when
    // pathfinding subsequently fails (the leader likely just stepped through a transition).
    private (int x, int y)? _lastPathfindableLeaderGrid;
    private (int x, int y)? _portalSearchReference;
    private int _portalClickAttempts;
    private DateTime _nextPortalClickAt = DateTime.MinValue;

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

        EventBus.Instance.Subscribe<RenderEvent>(HandleRender);
    }

    public bool Update()
    {
        var settings = FollowHer.Instance.Settings.Movement;
        if (!settings.Enable) return false;

        var leaderName = FollowHer.Instance.Settings.LeaderName.Value;
        if (string.IsNullOrWhiteSpace(leaderName)) return false;

        var player = _gameController.Player;
        if (player == null || !player.IsAlive) return false;

        try
        {
            var currentArea = _gameController.Area?.CurrentArea;
            // Disabling movement in town only blocks normal same-zone following - it must not
            // block the leader-left-town case, so the zone-transition check below still runs.
            var blockMovementInTown = settings.DisableMovementInTown &&
                                       (currentArea?.IsTown == true || currentArea?.IsHideout == true);

            var leaderEntity = LeaderLocator.FindLeaderEntity(_gameController, leaderName);

            if (!blockMovementInTown)
            {
                var taskContext = new FollowTaskContext(_gameController, player, this, leaderEntity);
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
                targetGrid = RoundGrid(leaderEntity.GridPosNum);
                _lastKnownLeaderGrid = targetGrid;
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

                if (!shouldMove)
                {
                    StopMovement();
                    return true;
                }

                var hasLineOfSight = HasClearLineOfSight(player.GridPosNum, leaderEntity.GridPosNum);
                if (DeterminePursuitMode(hasLineOfSight) == PursuitMode.Direct)
                {
                    return ExecuteMovement(leaderEntity.PosNum, leaderEntity.GridPosNum);
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

    private static (int x, int y) RoundGrid(Vector2 gridPos)
    {
        return ((int)Math.Round(gridPos.X), (int)Math.Round(gridPos.Y));
    }

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

        if (!settings.EnablePathfindingFallback)
        {
            // A* disabled - always walk straight at the (possibly obstructed) target.
            return ExecuteMovement(GridToWorldPosition(target.x, target.y), new Vector2(target.x, target.y));
        }

        var grid = _lineOfSight.GetGrid(LineOfSightDataType.Walkable);
        if (grid == null)
        {
            // No terrain data yet - fall back to a direct walk toward the target.
            return ExecuteMovement(GridToWorldPosition(target.x, target.y), new Vector2(target.x, target.y));
        }

        if (NeedsPathRecompute(target))
        {
            var rawPath = GridPathfinder.AStar(grid, start, target, MaxPathSearchNodes);
            _currentPath = rawPath is { Count: > 0 } ? SmoothPath(rawPath) : null;
            _currentPathIndex = 0;
            _currentPathTarget = target;
            _nextPathRecomputeAt = DateTime.Now.AddMilliseconds(MinPathRecomputeIntervalMs);

            if (_currentPath != null)
            {
                _lastPathfindableLeaderGrid = target;
                LogDebug($"Computed path to leader with {_currentPath.Count} waypoint(s)");
            }
            else
            {
                LogDebug("No path found to leader's position - they may have just gone through a transition");
            }
        }

        if (_currentPath == null)
        {
            return TryFollowThroughRecentPortal(player.PosNum, target);
        }

        while (_currentPathIndex < _currentPath.Count)
        {
            var node = _currentPath[_currentPathIndex];
            var nodeWorld = GridToWorldPosition(node.x, node.y);

            if (Vector3.Distance(player.PosNum, nodeWorld) <= settings.KeepWithinDistance * 1.5f)
            {
                _currentPathIndex++;
                continue;
            }

            return ExecuteMovement(nodeWorld, new Vector2(node.x, node.y));
        }

        if (!leaderVisible)
        {
            // Same as above - reached the end of the path to the leader's last known position
            // and they're still not visible, so look for a nearby transition instead of idling.
            return TryFollowThroughRecentPortal(player.PosNum, target);
        }

        StopMovement();
        return true;
    }

    private bool NeedsPathRecompute((int x, int y) target)
    {
        if (_currentPathTarget == null) return true;
        if (_currentPath == null) return DateTime.Now >= _nextPathRecomputeAt;
        if (_currentPathIndex >= _currentPath.Count) return true;

        var drift = Vector2.Distance(
            new Vector2(target.x, target.y),
            new Vector2(_currentPathTarget.Value.x, _currentPathTarget.Value.y));

        if (drift <= PathRetargetThresholdGrid) return false;

        return DateTime.Now >= _nextPathRecomputeAt;
    }

    // Pathfinding to the leader's believed position just failed while we still think we're in the
    // same zone - most likely they stepped through a transition the party panel hasn't caught up
    // to reporting yet. Look for a portal near the last position we could actually reach, rather
    // than near the player's own position.
    private bool TryFollowThroughRecentPortal(Vector3 playerPos, (int x, int y) fallbackTarget)
    {
        if (_lastPathfindableLeaderGrid == null)
        {
            // Never successfully pathed to the leader yet this zone - best-effort direct walk.
            return ExecuteMovement(GridToWorldPosition(fallbackTarget.x, fallbackTarget.y), new Vector2(fallbackTarget.x, fallbackTarget.y));
        }

        var referenceGrid = _lastPathfindableLeaderGrid.Value;

        if (_portalSearchReference != referenceGrid)
        {
            _portalSearchReference = referenceGrid;
            _portalClickAttempts = 0;
            _nextPortalClickAt = DateTime.MinValue;
        }

        if (_portalClickAttempts >= MaxFollowPortalAttempts)
        {
            LogDebug("Gave up looking for a transition near the leader's last reachable position");
            StopMovement();
            return true;
        }

        var referenceWorld = GridToWorldPosition(referenceGrid.x, referenceGrid.y);

        if (Vector3.Distance(playerPos, referenceWorld) > FollowPortalWalkTolerance)
        {
            return ExecuteMovement(referenceWorld, new Vector2(referenceGrid.x, referenceGrid.y));
        }

        var portal = FindNearbyPortal(referenceWorld, FollowPortalSearchRadius);
        if (portal == null)
        {
            StopMovement();
            return true;
        }

        var distanceToPortal = Vector3.Distance(playerPos, portal.ItemOnGround.PosNum);
        var approachDistance = FollowHer.Instance.Settings.Movement.PortalApproachDistance.Value;
        if (distanceToPortal > approachDistance)
        {
            return ExecuteMovement(portal.ItemOnGround.PosNum, portal.ItemOnGround.GridPosNum);
        }

        if (DateTime.Now < _nextPortalClickAt) return true;

        LogDebug("Found a transition near the leader's last reachable position - clicking it");
        // Release any held movement input first - a still-held Move key/mouse button can swallow
        // or override the click that follows.
        StopMovement();
        ClickElement(portal.Label);
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

        // Prefer walking to and clicking the matching zone transition over teleporting - only
        // teleport if it's too far to bother walking to, or we can't see one at all.
        var playerPos = _gameController.Player.PosNum;
        var portal = GetBestPortalLabel(leaderPartyMember.ZoneName, referencePosition);
        var maxWalkDistance = FollowHer.Instance.Settings.Movement.ZoneTransitionMaxWalkDistance.Value;

        // A portal matching the target zone's name isn't necessarily the SAME connection the
        // leader actually used - a zone can have multiple distinct transitions to another zone
        // that lead to different regions of it (e.g. we die and respawn at the zone's entrance,
        // which has its own transition to the next zone, while the leader actually went through a
        // different transition elsewhere). Only trust the portal-walk path when the candidate is
        // close to BOTH where we are now AND where the leader was last confirmed - if we just
        // respawned somewhere else, the nearby-to-us candidate will fail this second check and we
        // correctly fall back to teleporting straight to the leader instead.
        var portalNearReference = portal != null &&
                                   Vector3.Distance(referencePosition, portal.ItemOnGround.PosNum) <= maxWalkDistance;

        if (portal != null && portalNearReference && Vector3.Distance(playerPos, portal.ItemOnGround.PosNum) <= maxWalkDistance)
        {
            return TryWalkToZoneTransitionPortal(portal, playerPos);
        }

        LogDebug(portal == null
            ? "No zone transition visible - falling back to teleport"
            : "Zone transition is too far from where we are or from the leader's last known position - falling back to teleport");
        return TryTeleportToLeader(leaderPartyMember, referencePosition);
    }

    private bool TryWalkToZoneTransitionPortal(LabelOnGround portal, Vector3 playerPos)
    {
        var distanceToPortal = Vector3.Distance(playerPos, portal.ItemOnGround.PosNum);
        var approachDistance = FollowHer.Instance.Settings.Movement.PortalApproachDistance.Value;
        if (distanceToPortal > approachDistance)
        {
            return ExecuteMovement(portal.ItemOnGround.PosNum, portal.ItemOnGround.GridPosNum);
        }

        if (DateTime.Now < _nextPortalClickAt) return true;

        LogDebug("Walking to and clicking the zone transition");
        // Release any held movement input first - a still-held Move key/mouse button can swallow
        // or override the click that follows.
        StopMovement();
        ClickElement(portal.Label);
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

    private LabelOnGround GetBestPortalLabel(string leaderZoneName, Vector3 referencePosition)
    {
        var currentArea = _gameController.Area?.CurrentArea;
        var isHideout = currentArea?.IsHideout ?? false;
        var realLevel = currentArea?.RealLevel ?? 0;

        var candidates = GetPortalCandidates().ToList();
        if (candidates.Count == 0) return null;

        if (isHideout || realLevel >= 68)
        {
            var portals = candidates.OrderBy(x => Vector3.Distance(referencePosition, x.ItemOnGround.PosNum)).ToList();
            return isHideout ? portals[_random.Next(portals.Count)] : portals[0];
        }

        if (string.IsNullOrEmpty(leaderZoneName)) return null;

        return candidates
            .Where(x => x.Label.Text != null && x.Label.Text.Contains(leaderZoneName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => Vector3.Distance(referencePosition, x.ItemOnGround.PosNum))
            .FirstOrDefault();
    }

    private LabelOnGround FindNearbyPortal(Vector3 referencePosition, float maxDistance)
    {
        return GetPortalCandidates()
            .Where(x => Vector3.Distance(referencePosition, x.ItemOnGround.PosNum) < maxDistance)
            .OrderBy(x => Vector3.Distance(referencePosition, x.ItemOnGround.PosNum))
            .FirstOrDefault();
    }

    private List<(int x, int y)> SmoothPath(List<(int x, int y)> rawPath)
    {
        var smoothed = new List<(int x, int y)>();
        var i = 0;

        while (i < rawPath.Count)
        {
            var best = i;
            for (var j = Math.Min(rawPath.Count - 1, i + PathSmoothingLookahead); j > i; j--)
            {
                try
                {
                    var from = new Vector2(rawPath[i].x, rawPath[i].y);
                    var to = new Vector2(rawPath[j].x, rawPath[j].y);
                    if (_lineOfSight.HasLineOfSight(from, to, LineOfSightDataType.Walkable))
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

    private Vector3 GridToWorldPosition(int x, int y)
    {
        var gridPos = new Vector2(x, y);
        var worldXy = gridPos.GridToWorld();
        var z = _gameController.IngameState.Data.GetTerrainHeightAt(gridPos);
        return new Vector3(worldXy.X, worldXy.Y, z);
    }

    // Default path is teleport-to-player: click the party panel's TP button, then confirm the
    // popup it opens. Only if that popup never shows up (TP unavailable/failed) do we fall back
    // to hunting for the zone-transition portal near the leader's last reachable position.
    private bool TryTeleportToLeader(PartyMemberInfo leaderPartyMember, Vector3 referencePosition)
    {
        if (DateTime.Now < _nextTpActionAt) return true;

        var confirmationButton = GetTpConfirmationButton();
        if (confirmationButton != null)
        {
            LogDebug("Teleport confirmation window open - confirming");
            StopMovement();
            ClickElement(confirmationButton);
            _tpButtonClickedAt = null;
            _nextTpActionAt = DateTime.Now.AddMilliseconds(500);
            return true;
        }

        if (_tpButtonClickedAt.HasValue)
        {
            if (DateTime.Now - _tpButtonClickedAt.Value < TimeSpan.FromMilliseconds(TpConfirmationTimeoutMs))
            {
                // Already clicked TP, still waiting to see whether a confirmation window appears.
                return true;
            }

            LogDebug("Teleport-to-player produced no confirmation window - falling back to the zone transition portal");
            _tpButtonClickedAt = null;
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
        var portal = GetBestPortalLabel(leaderZoneName, referencePosition);
        if (portal == null) return true;

        LogDebug($"Found matching portal for zone '{leaderZoneName}' - clicking");
        StopMovement();
        ClickElement(portal.Label);
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
    private void StopMovement()
    {
        _skillHandler.ReleaseAllSkills();
        _nextMovementInputAt = DateTime.MinValue;
    }

    internal bool ExecuteMovement(Vector3 targetWorldPosition, Vector2? targetGridPosition = null)
    {
        var screenPos = _gameController.IngameState.Camera.WorldToScreen(targetWorldPosition);
        if (screenPos == Vector2.Zero) return false;

        _lastMoveTargetWorld = targetWorldPosition;

        if (DateTime.Now < _nextMovementInputAt) return true;

        var movementSkill = targetGridPosition.HasValue
            ? TryGetMovementSkill(_gameController.Player.GridPosNum, targetGridPosition.Value)
            : null;

        using (ExileCore.Input.InputManager.BlockUserMouseInput())
        {
            ExileCore.Input.InputManager.MoveMouse(screenPos);
            _skillHandler.UseMovementSkill(movementSkill?.Name ?? MoveSkillName, true);
        }

        if (movementSkill != null)
        {
            LogDebug($"Using movement skill '{movementSkill.Name}' toward {targetWorldPosition}");
        }

        var inputFrequency = FollowHer.Instance.Settings.Movement.InputFrequency.Value;
        _nextMovementInputAt = DateTime.Now.AddMilliseconds(inputFrequency);

        return true;
    }

    // Movement skills come in two mechanically different flavors, and each setting now maps onto
    // exactly one of them (see ActiveSkill.TravelsThroughObstacles):
    //   - DashEnabled -> blink-type skills (Frostblink/FlameDash/LightningWarp/BlinkArrow), which
    //     ignore obstacles entirely and only need LOS - so they're only useful to punch through a
    //     blocked path, never as a plain speed boost on ground that's already clear.
    //   - PreferMovementSkillsForTravel -> ground-dash skills (Shield Charge/Whirling Blades),
    //     which collide with obstacles like a moving hitbox - a thin center-line LOS pass isn't
    //     enough to guarantee they won't clip a corner, so they additionally require a full
    //     corridor clearance check (GridPathfinder.HasCorridorClearance) across the configured
    //     hitbox margin, and are only used when the path is already clear - never to punch
    //     through something, since they physically can't.
    private ActiveSkill TryGetMovementSkill(Vector2 playerGrid, Vector2 targetGrid)
    {
        var settings = FollowHer.Instance.Settings.Movement;
        if (!settings.DashEnabled && !settings.PreferMovementSkillsForTravel) return null;

        var distance = Vector2.Distance(playerGrid, targetGrid);
        if (distance < DashMinGridDistance || distance > DashMaxGridDistance) return null;

        var hasClearLineOfSight = HasClearLineOfSight(playerGrid, targetGrid);

        if (settings.DashEnabled && !hasClearLineOfSight)
        {
            var blinkSkill = FollowHer.Instance.Settings.Movement.MovementSkills.Content
                .FirstOrDefault(s => s.Enabled && s.Name != MoveSkillName && s.TravelsThroughObstacles &&
                                      _skillMonitor.CanUseSkill(s));
            if (blinkSkill != null) return blinkSkill;
        }

        if (settings.PreferMovementSkillsForTravel && hasClearLineOfSight)
        {
            var grid = _lineOfSight.GetGrid(LineOfSightDataType.Walkable);
            if (grid != null && GridPathfinder.HasCorridorClearance(grid, playerGrid, targetGrid, settings.MovementSkillClearanceMargin))
            {
                var dashSkill = FollowHer.Instance.Settings.Movement.MovementSkills.Content
                    .FirstOrDefault(s => s.Enabled && s.Name != MoveSkillName && !s.TravelsThroughObstacles &&
                                          _skillMonitor.CanUseSkill(s));
                if (dashSkill != null) return dashSkill;
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
        ClearState();
    }

    public void Stop()
    {
        ClearState();
    }

    private void ClearState()
    {
        _lastKnownLeaderGrid = null;
        _currentPath = null;
        _currentPathIndex = 0;
        _currentPathTarget = null;
        _nextPathRecomputeAt = DateTime.MinValue;
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

        foreach (var task in _tasks)
        {
            task.Reset();
        }
    }
}
