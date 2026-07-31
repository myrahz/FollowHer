using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace FollowHer.Features.Following.Tasks;

/// <summary>Clicks nearby EntityType.IngameIcon world objects (levers, quest triggers, etc.) the
/// leader has been near. Same two behaviours as QuestItemPickupTask: "leader was near it" is sticky
/// (commit even after the leader moves off / leaves the zone), and when the object has a visible
/// label with a walkable path we just click and let the game walk there instead of pathfinding to it
/// ourselves. Objects with no ground label fall back to approach-then-click at their world position.</summary>
public class QuestObjectClickTask : IFollowTask
{
    private const int ClickCooldownMs = 4000;
    private const int CommitmentTimeoutMs = 20000;
    private const float InteractionApproachDistance = 150f;

    private long _currentObjectAddress;
    private DateTime _nextClickAt = DateTime.MinValue;
    private DateTime _commitmentExpiresAt = DateTime.MaxValue;

    private readonly HashSet<long> _leaderWasNear = new();
    private readonly HashSet<long> _abandoned = new();

    public bool TryExecute(FollowTaskContext context)
    {
        if (!FollowHer.Instance.Settings.Movement.Tasks.ClickQuestObjects) return false;

        var objects = FindQuestObjects(context.GameController);

        // Remember which objects the leader is/was near (live position, or last-seen once gone).
        var anchor = context.LeaderProximityAnchor;
        if (anchor != null)
        {
            var range = FollowHer.Instance.Settings.Movement.Tasks.LeaderProximityRange.Value;
            foreach (var o in objects)
                if (Vector3.Distance(anchor.Value, o.PosNum) <= range)
                    _leaderWasNear.Add(o.Address);
        }

        var target = _currentObjectAddress != 0
            ? objects.FirstOrDefault(o => o.Address == _currentObjectAddress)
            : null;

        if (target == null && _currentObjectAddress != 0)
            ClearCommitment();

        if (target == null)
        {
            target = AcquireTarget(context, objects);
            if (target == null) return false;

            _currentObjectAddress = target.Address;
            _nextClickAt = DateTime.MinValue;
            _commitmentExpiresAt = DateTime.Now.AddMilliseconds(CommitmentTimeoutMs);
        }

        if (DateTime.Now > _commitmentExpiresAt)
        {
            context.Manager.LogDebug("Gave up clicking quest object - took too long");
            Abandon();
            return false;
        }

        var label = FindGroundLabel(context.GameController, target);
        var labelVisible = label?.Label is { IsValid: true, IsVisible: true };

        // The click may legitimately start the game's own click-to-move. Give it enough time to
        // finish without our approach/settling code cancelling or redirecting that movement.
        if (DateTime.Now < _nextClickAt) return true;

        var distanceToObject = Vector3.Distance(context.Player.PosNum, target.PosNum);
        if (distanceToObject > InteractionApproachDistance)
        {
            context.Manager.ResetInteractionSettling();
            return context.Manager.ExecuteMovement(target.PosNum, target.GridPosNum);
        }

        // We stop at the approach radius above, rather than trying to occupy the object's often
        // blocked cell. Once there, wait for the camera to settle before resolving the label's
        // screen rectangle and clicking it.
        if (!context.Manager.IsPlayerSettledForInteraction()) return true;

        if (labelVisible)
        {
            context.Manager.LogDebug($"Clicking quest object '{target.Metadata}'");
            context.Manager.ClickElement(label.Label);
            _nextClickAt = DateTime.Now.AddMilliseconds(ClickCooldownMs);
            return true;
        }

        // No label - click its world position, but only when it's on screen (WorldToScreen returns
        // Zero for off-screen points, where the click would do nothing).
        var screenPos = context.GameController.IngameState.Camera.WorldToScreen(target.PosNum);
        if (screenPos != Vector2.Zero)
        {
            context.Manager.LogDebug($"Clicking quest object '{target.Metadata}' (world position)");
            context.Manager.ClickWorldPosition(target.PosNum);
            _nextClickAt = DateTime.Now.AddMilliseconds(ClickCooldownMs);
            return true;
        }

        // Off-screen with no label - close the distance so it comes into view, then click next tick.
        return context.Manager.ExecuteMovement(target.PosNum, target.GridPosNum);
    }

    private Entity AcquireTarget(FollowTaskContext context, List<Entity> objects)
    {
        var clickRange = FollowHer.Instance.Settings.Movement.Tasks.QuestObjectClickRange.Value;
        var playerPos = context.Player.PosNum;

        return objects
            .Where(o => !_abandoned.Contains(o.Address) &&
                        _leaderWasNear.Contains(o.Address) &&
                        Vector3.Distance(playerPos, o.PosNum) < clickRange)
            .OrderBy(o => Vector3.Distance(playerPos, o.PosNum))
            .FirstOrDefault();
    }

    private void Abandon()
    {
        if (_currentObjectAddress != 0) _abandoned.Add(_currentObjectAddress);
        ClearCommitment();
    }

    private void ClearCommitment()
    {
        _currentObjectAddress = 0;
        _nextClickAt = DateTime.MinValue;
        _commitmentExpiresAt = DateTime.MaxValue;
    }

    public void Reset()
    {
        ClearCommitment();
        _leaderWasNear.Clear();
        _abandoned.Clear();
    }

    // EntityType.IngameIcon is a broad bucket. Any path substring listed here is skipped - a place
    // to blacklist specific objects the follower shouldn't auto-click. Empty by default (clicking
    // icon objects is wanted); add substrings here only to exclude specific ones later.
    private static readonly string[] IgnoredMetadataSubstrings = { };

    private static List<Entity> FindQuestObjects(GameController gameController)
    {
        try
        {
            return gameController.EntityListWrapper.ValidEntitiesByType.GetValueOrDefault(EntityType.IngameIcon)?
                .Where(x => x is { IsValid: true, IsTargetable: true } && !IsIgnoredObject(x))
                .ToList() ?? new List<Entity>();
        }
        catch
        {
            return new List<Entity>();
        }
    }

    private static bool IsIgnoredObject(Entity entity)
    {
        var metadata = entity.Metadata;
        if (string.IsNullOrEmpty(metadata)) return false;
        return IgnoredMetadataSubstrings.Any(s => metadata.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    private static LabelOnGround FindGroundLabel(GameController gameController, Entity entity)
    {
        return gameController.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?
            .FirstOrDefault(x => x.IsVisible && x.ItemOnGround?.Address == entity.Address);
    }
}
