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
using FollowHer.Settings;
using FollowHer.Utils;

namespace FollowHer.Features.Following;

public class FollowManager
{
    private const string MoveSkillName = "Move";
    private const float LeaderJumpRecentSeconds = 10f;
    private const float LeaderJumpForgetSeconds = 15f;
    private const float FollowPortalWalkTolerance = 30f;
    private const float FollowPortalSearchRadius = 100f;
    private const float FollowPortalApproachTolerance = 50f;
    private const float LeaderJumpPortalSearchRadius = 150f;
    private const int MaxFollowPortalAttempts = 10;
    private const int MaxPathSearchNodes = 20000;
    private const int PathSmoothingLookahead = 6;
    private const float DashMinGridDistance = 30f;
    private const float DashMaxGridDistance = 150f;

    private readonly GameController _gameController;
    private readonly SkillHandler _skillHandler;
    private readonly SkillMonitor _skillMonitor;
    private readonly LineOfSight _lineOfSight;
    private readonly Random _random = new();

    // Updated whenever the leader entity is visible - target for the last-known-position
    // pathfinding fallback below.
    private (int x, int y)? _lastKnownLeaderGrid;

    private List<(int x, int y)> _currentPath;
    private int _currentPathIndex;
    private (int x, int y)? _currentPathTarget;

    // Updated only while the leader is within LeaderNearbyDistance - the recovery waypoint used
    // by the leader-jump/FollowLeaderPortal heuristic below.
    private Vector3? _lastNearbyLeaderPosition;
    private Vector2? _lastNearbyLeaderGrid;

    private string _lastKnownLeaderZone = "";
    private DateTime _leaderZoneChangeTime = DateTime.MinValue;
    private DateTime _nextTpActionAt = DateTime.MinValue;

    private float _previousLeaderDistance;
    private bool _leaderWasNearby;
    private DateTime _lastLeaderNearbyTime = DateTime.MinValue;
    private FollowTaskNode _activeFollowPortalTask;

    private DateTime _nextMovementInputAt = DateTime.MinValue;
    private Vector3? _lastMoveTargetWorld;

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
        var settings = FollowHer.Instance.Settings.Combat.Follow;
        if (!settings.Enable) return false;

        var leaderName = FollowHer.Instance.Settings.LeaderName.Value;
        if (string.IsNullOrWhiteSpace(leaderName)) return false;

        var player = _gameController.Player;
        if (player == null || !player.IsAlive) return false;

        try
        {
            var leaderEntity = LeaderLocator.FindLeaderEntity(_gameController, leaderName);
            float? distanceToLeader = null;

            if (leaderEntity != null)
            {
                _lastKnownLeaderZone = "";
                _leaderZoneChangeTime = DateTime.MinValue;
                _lastKnownLeaderGrid = (
                    (int)Math.Round(leaderEntity.GridPosNum.X),
                    (int)Math.Round(leaderEntity.GridPosNum.Y));

                distanceToLeader = Vector3.Distance(player.PosNum, leaderEntity.PosNum);
                UpdateLeaderJumpTracking(distanceToLeader.Value, leaderEntity.PosNum, leaderEntity.GridPosNum, player.PosNum, settings);
            }

            if (_activeFollowPortalTask != null)
            {
                return ProcessFollowLeaderPortalTask(distanceToLeader ?? float.MaxValue, player.PosNum, settings);
            }

            if (leaderEntity != null)
            {
                var shouldMove = distanceToLeader >= settings.TransitionDistance ||
                                  (distanceToLeader >= settings.KeepWithinDistance && settings.CloseFollow);

                LogDebug($"Leader visible, distance={distanceToLeader:F0}, shouldMove={shouldMove}");

                if (!shouldMove)
                {
                    StopMovement();
                    return true;
                }

                return ExecuteMovement(leaderEntity.PosNum, leaderEntity.GridPosNum);
            }

            // Leader entity isn't in our local entity list - either they're in a different zone
            // (handled below via the party panel) or just out of range in the same zone (handled
            // by the last-known-position pathfinding fallback).
            var leaderPartyMember = PartyPanelScanner.GetLeaderPartyMember(_gameController, leaderName);
            var currentZone = _gameController.Area?.CurrentArea?.DisplayName;

            if (leaderPartyMember != null && !string.Equals(leaderPartyMember.ZoneName, currentZone, StringComparison.Ordinal))
            {
                LogDebug($"Leader not visible, reported in different zone '{leaderPartyMember.ZoneName}' (current '{currentZone}') - attempting transition");
                return TryFollowThroughZoneTransition(leaderPartyMember, currentZone);
            }

            LogDebug($"Leader not visible, same zone - {(_lastKnownLeaderGrid != null ? "using last-known-position fallback" : "no last-known position yet")}");
            return TryFollowLastKnownPosition(player, settings);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[FollowManager] Error during update: {ex.Message}");
            return false;
        }
    }

    private void UpdateLeaderJumpTracking(float distanceToLeader, Vector3 leaderPos, Vector2 leaderGrid, Vector3 playerPos, CombatSettings.FollowSettings settings)
    {
        if (distanceToLeader < settings.LeaderNearbyDistance)
        {
            _leaderWasNearby = true;
            _lastLeaderNearbyTime = DateTime.Now;
            _lastNearbyLeaderPosition = leaderPos;
            _lastNearbyLeaderGrid = leaderGrid;
        }

        // Leader was close very recently and is now suddenly very far - most likely they just
        // stepped through a portal near where they were standing. If there's a portal near us
        // too, assume it's the same one and try to follow through it.
        if (_leaderWasNearby &&
            distanceToLeader > settings.LeaderJumpDistance &&
            _previousLeaderDistance < settings.LeaderNearbyDistance &&
            (DateTime.Now - _lastLeaderNearbyTime).TotalSeconds < LeaderJumpRecentSeconds &&
            _activeFollowPortalTask == null)
        {
            var nearbyPortal = FindNearbyPortal(playerPos, LeaderJumpPortalSearchRadius);
            if (nearbyPortal != null)
            {
                LogDebug($"Leader distance jumped from {_previousLeaderDistance:F0} to {distanceToLeader:F0} - following through nearby portal");
                _activeFollowPortalTask = new FollowTaskNode(nearbyPortal, settings.KeepWithinDistance, FollowTaskType.FollowLeaderPortal);
                _leaderWasNearby = false;
            }
        }

        if (distanceToLeader > settings.LeaderJumpDistance && (DateTime.Now - _lastLeaderNearbyTime).TotalSeconds > LeaderJumpForgetSeconds)
        {
            _leaderWasNearby = false;
        }

        _previousLeaderDistance = distanceToLeader;
    }

    private bool ProcessFollowLeaderPortalTask(float distanceToLeader, Vector3 playerPos, CombatSettings.FollowSettings settings)
    {
        var task = _activeFollowPortalTask;

        // Success: leader is back within range - the portal worked.
        if (distanceToLeader < settings.LeaderNearbyDistance)
        {
            _activeFollowPortalTask = null;
            return false;
        }

        if (task.AttemptCount >= MaxFollowPortalAttempts)
        {
            _activeFollowPortalTask = null;
            return false;
        }

        if (DateTime.Now < task.NextAttemptAt) return true;

        // Stage 1: walk to where the leader was last seen nearby.
        var lastNearbyPos = _lastNearbyLeaderPosition ?? playerPos;
        var distanceToLastLeaderPos = Vector3.Distance(playerPos, lastNearbyPos);
        if (distanceToLastLeaderPos > FollowPortalWalkTolerance)
        {
            return ExecuteMovement(lastNearbyPos, _lastNearbyLeaderGrid);
        }

        // Stage 2: find and click a portal near that position.
        var nearbyPortal = FindNearbyPortal(playerPos, FollowPortalSearchRadius);
        if (nearbyPortal == null)
        {
            _activeFollowPortalTask = null;
            return false;
        }

        var distanceToPortal = Vector3.Distance(playerPos, nearbyPortal.ItemOnGround.PosNum);
        if (distanceToPortal > FollowPortalApproachTolerance)
        {
            return ExecuteMovement(nearbyPortal.ItemOnGround.PosNum, nearbyPortal.ItemOnGround.GridPosNum);
        }

        ClickElement(nearbyPortal.Label);
        task.AttemptCount++;
        task.NextAttemptAt = DateTime.Now.AddMilliseconds(2500);
        return true;
    }

    private bool TryFollowThroughZoneTransition(PartyMemberInfo leaderPartyMember, string currentZone)
    {
        if (!string.Equals(_lastKnownLeaderZone, leaderPartyMember.ZoneName, StringComparison.Ordinal))
        {
            _lastKnownLeaderZone = leaderPartyMember.ZoneName;
            _leaderZoneChangeTime = DateTime.Now;
        }

        if (!IsLeaderZoneInfoReliable(leaderPartyMember, currentZone))
        {
            // Zone text just changed and may still be mid-update - wait for it to settle
            // before acting on it, rather than chasing a stale/half-written zone name.
            return true;
        }

        var referencePosition = _lastNearbyLeaderPosition ?? _gameController.Player.PosNum;
        var portal = GetBestPortalLabel(leaderPartyMember.ZoneName, referencePosition);
        if (portal != null)
        {
            LogDebug($"Found matching portal for zone '{leaderPartyMember.ZoneName}' - clicking");
            ClickElement(portal.Label);
            return true;
        }

        LogDebug($"No matching portal found for zone '{leaderPartyMember.ZoneName}' - falling back to party teleport button");
        return TryTeleportToLeader(leaderPartyMember);
    }

    private bool IsLeaderZoneInfoReliable(PartyMemberInfo leaderPartyMember, string currentZone)
    {
        var zoneName = leaderPartyMember.ZoneName;
        if (string.IsNullOrEmpty(zoneName) || string.Equals(zoneName, currentZone, StringComparison.Ordinal))
            return false;

        var timeSinceChange = DateTime.Now - _leaderZoneChangeTime;
        var bufferMs = FollowHer.Instance.Settings.Combat.Follow.ZoneUpdateBuffer.Value;
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

    private bool TryFollowLastKnownPosition(Entity player, CombatSettings.FollowSettings settings)
    {
        if (!settings.EnablePathfindingFallback || _lastKnownLeaderGrid == null) return false;

        var target = _lastKnownLeaderGrid.Value;
        var start = ((int)Math.Round(player.GridPosNum.X), (int)Math.Round(player.GridPosNum.Y));
        if (start == target)
        {
            StopMovement();
            return true;
        }

        var grid = _lineOfSight.GetGrid(LineOfSightDataType.Walkable);
        if (grid == null)
        {
            // No terrain data yet - fall back to a direct walk toward the last-known point.
            return ExecuteMovement(GridToWorldPosition(target.x, target.y), new Vector2(target.x, target.y));
        }

        if (_currentPath == null || _currentPathIndex >= _currentPath.Count || _currentPathTarget != target)
        {
            var rawPath = GridPathfinder.AStar(grid, start, target, MaxPathSearchNodes);
            _currentPath = rawPath is { Count: > 0 } ? SmoothPath(rawPath) : null;
            _currentPathIndex = 0;
            _currentPathTarget = target;
            LogDebug(_currentPath != null
                ? $"Computed path to last-known leader position with {_currentPath.Count} waypoint(s)"
                : "No path found to last-known leader position - falling back to direct walk");
        }

        if (_currentPath == null)
        {
            // No path found (e.g. the leader's last spot isn't reachable) - direct walk fallback.
            return ExecuteMovement(GridToWorldPosition(target.x, target.y), new Vector2(target.x, target.y));
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

        StopMovement();
        return true;
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

    private bool TryTeleportToLeader(PartyMemberInfo leaderPartyMember)
    {
        if (DateTime.Now < _nextTpActionAt) return true;

        var confirmationButton = GetTpConfirmationButton();
        if (confirmationButton != null)
        {
            ClickElement(confirmationButton);
            _nextTpActionAt = DateTime.Now.AddMilliseconds(500);
            return true;
        }

        if (leaderPartyMember.TpButton == null) return false;

        ClickElement(leaderPartyMember.TpButton);
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

    private void ClickElement(Element element)
    {
        if (element == null) return;

        var position = element.GetClientRect().ClickRandomNum(5, 3) +
                        _gameController.Window.GetWindowRectangle().TopLeft.ToVector2Num();

        ExileCore.Input.SetCursorPos(position);
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

    private bool ExecuteMovement(Vector3 targetWorldPosition, Vector2? targetGridPosition = null)
    {
        var screenPos = _gameController.IngameState.Camera.WorldToScreen(targetWorldPosition);
        if (screenPos == Vector2.Zero) return false;

        _lastMoveTargetWorld = targetWorldPosition;

        if (DateTime.Now < _nextMovementInputAt) return true;

        var dashSkill = targetGridPosition.HasValue
            ? TryGetDashSkill(_gameController.Player.GridPosNum, targetGridPosition.Value)
            : null;

        using (ExileCore.Input.InputManager.BlockUserMouseInput())
        {
            ExileCore.Input.InputManager.MoveMouse(screenPos);
            _skillHandler.UseMovementSkill(dashSkill?.Name ?? MoveSkillName, true);
        }

        if (dashSkill != null)
        {
            LogDebug($"Using dash skill '{dashSkill.Name}' toward {targetWorldPosition}");
        }

        var inputFrequency = FollowHer.Instance.Settings.Combat.Follow.InputFrequency.Value;
        _nextMovementInputAt = DateTime.Now.AddMilliseconds(inputFrequency);

        return true;
    }

    // Prefer an enabled movement skill (e.g. a leap/dash) over plain "Move" specifically when the
    // target is a short-to-medium distance away AND there's no direct line of sight - i.e. likely
    // blocked by an obstacle/door - mirroring AreWeThereYet's ShouldUseDash heuristic.
    private ActiveSkill TryGetDashSkill(Vector2 playerGrid, Vector2 targetGrid)
    {
        if (!FollowHer.Instance.Settings.Combat.Follow.DashEnabled) return null;

        var distance = Vector2.Distance(playerGrid, targetGrid);
        if (distance < DashMinGridDistance || distance > DashMaxGridDistance) return null;

        bool hasLineOfSight;
        try
        {
            hasLineOfSight = _lineOfSight.HasLineOfSight(playerGrid, targetGrid, LineOfSightDataType.Walkable);
        }
        catch
        {
            return null;
        }

        if (hasLineOfSight) return null;

        return FollowHer.Instance.Settings.Combat.MovementSkills.Content
            .FirstOrDefault(s => s.Enabled && s.Name != MoveSkillName && _skillMonitor.CanUseSkill(s));
    }

    private void HandleRender(RenderEvent evt)
    {
        var visual = FollowHer.Instance.Settings.Combat.Follow.Visual;
        if (!visual.ShowFollowPath || _lastMoveTargetWorld == null) return;

        var player = _gameController.Player;
        if (player == null) return;

        var playerScreen = _gameController.IngameState.Camera.WorldToScreen(player.PosNum);
        var targetScreen = _gameController.IngameState.Camera.WorldToScreen(_lastMoveTargetWorld.Value);
        if (playerScreen == Vector2.Zero || targetScreen == Vector2.Zero) return;

        evt.Graphics.DrawLine(playerScreen, targetScreen, visual.TaskLineWidth.Value, visual.TaskLineColor.Value);
    }

    private void LogDebug(string message)
    {
        if (FollowHer.Instance.Settings.Combat.Follow.Debug.ShowDetailedDebug)
        {
            DebugWindow.LogMsg($"[Follow] {message}");
        }
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
        _lastNearbyLeaderPosition = null;
        _lastNearbyLeaderGrid = null;
        _lastKnownLeaderZone = "";
        _leaderZoneChangeTime = DateTime.MinValue;
        _nextTpActionAt = DateTime.MinValue;
        _previousLeaderDistance = 0f;
        _leaderWasNearby = false;
        _lastLeaderNearbyTime = DateTime.MinValue;
        _activeFollowPortalTask = null;
        _nextMovementInputAt = DateTime.MinValue;
        _lastMoveTargetWorld = null;
    }
}
