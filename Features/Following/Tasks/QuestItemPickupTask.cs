using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace FollowHer.Features.Following.Tasks;

/// <summary>Picks up quest items the leader has been near. Two behaviours matter here:
///
/// 1. "Leader was near it" is sticky - once the leader has been within range of a quest item, the
///    follower commits to grabbing it even after the leader moves off or steps through a transition.
///    Otherwise the follower would teleport after the leader without ever getting the quest state,
///    which it can't acquire from the other side.
///
/// 2. It doesn't pathfind to the item itself. As long as the label is visible and there's a walkable
///    path, it just clicks and lets the game's click-to-move walk the character there - issuing our
///    own movement on top of that fought the game and made the pickup stutter.</summary>
public class QuestItemPickupTask : IFollowTask
{
    private const int ClickCooldownMs = 800;
    private const int CommitmentTimeoutMs = 20000;

    private long _currentItemAddress;
    private DateTime _nextClickAt = DateTime.MinValue;
    private DateTime _commitmentExpiresAt = DateTime.MaxValue;

    // Quest items the leader has been near this zone - once listed, we go grab it regardless of
    // where the leader is now. Cleared on area change.
    private readonly HashSet<long> _leaderWasNear = new();
    // Items we gave up on, so we don't immediately re-acquire the same one.
    private readonly HashSet<long> _abandoned = new();

    public bool TryExecute(FollowTaskContext context)
    {
        if (!FollowHer.Instance.Settings.Movement.Tasks.PickUpQuestItems) return false;
        if (context.GameController.Area?.CurrentArea?.IsHideout == true) return false;

        var labels = FindQuestItemLabels(context.GameController);

        // Remember which quest items the leader is/was near (measured against the live leader
        // position, or where they were last seen once they're gone).
        var anchor = context.LeaderProximityAnchor;
        if (anchor != null)
        {
            var range = FollowHer.Instance.Settings.Movement.Tasks.LeaderProximityRange.Value;
            foreach (var l in labels)
            {
                if (Vector3.Distance(anchor.Value, l.ItemOnGround.PosNum) <= range)
                    _leaderWasNear.Add(l.ItemOnGround.Address);
            }
        }

        // Stay on the committed item as long as it's still on the ground.
        var target = _currentItemAddress != 0
            ? labels.FirstOrDefault(l => l.ItemOnGround.Address == _currentItemAddress)
            : null;

        if (target == null && _currentItemAddress != 0)
        {
            // Committed item is gone (picked up, or no longer labelled) - done with it.
            ClearCommitment();
        }

        if (target == null)
        {
            target = AcquireTarget(context, labels);
            if (target == null) return false;

            _currentItemAddress = target.ItemOnGround.Address;
            _nextClickAt = DateTime.MinValue;
            _commitmentExpiresAt = DateTime.Now.AddMilliseconds(CommitmentTimeoutMs);
        }

        if (DateTime.Now > _commitmentExpiresAt)
        {
            context.Manager.LogDebug("Gave up picking up quest item - took too long");
            Abandon();
            return false;
        }

        var item = target.ItemOnGround;
        var labelVisible = target.Label is { IsValid: true, IsVisible: true };

        if (labelVisible)
        {
            // Between clicks, hold position - the game is already walking us to the item from the
            // last click. Don't issue movement, or it fights the click-to-move.
            if (DateTime.Now < _nextClickAt) return true;

            if (context.Manager.HasWalkablePathTo(item.GridPosNum))
            {
                context.Manager.LogDebug($"Clicking quest item '{item.Metadata}'");
                context.Manager.ClickElement(target.Label);
                _nextClickAt = DateTime.Now.AddMilliseconds(ClickCooldownMs);
                return true;
            }
        }

        // Can't just click yet (item off-screen, or no walkable path the game could use) - close
        // the distance ourselves, then re-evaluate.
        return context.Manager.ExecuteMovement(item.PosNum, item.GridPosNum);
    }

    // Only quest items the leader has been near, within our pickup range, not already abandoned.
    private LabelOnGround AcquireTarget(FollowTaskContext context, List<LabelOnGround> labels)
    {
        var pickupRange = FollowHer.Instance.Settings.Movement.Tasks.QuestItemPickupRange.Value;
        var playerPos = context.Player.PosNum;

        return labels
            .Where(l => !_abandoned.Contains(l.ItemOnGround.Address) &&
                        _leaderWasNear.Contains(l.ItemOnGround.Address) &&
                        Vector3.Distance(playerPos, l.ItemOnGround.PosNum) < pickupRange)
            .OrderBy(l => Vector3.Distance(playerPos, l.ItemOnGround.PosNum))
            .FirstOrDefault();
    }

    private void Abandon()
    {
        if (_currentItemAddress != 0) _abandoned.Add(_currentItemAddress);
        ClearCommitment();
    }

    private void ClearCommitment()
    {
        _currentItemAddress = 0;
        _nextClickAt = DateTime.MinValue;
        _commitmentExpiresAt = DateTime.MaxValue;
    }

    public void Reset()
    {
        ClearCommitment();
        _leaderWasNear.Clear();
        _abandoned.Clear();
    }

    private static List<LabelOnGround> FindQuestItemLabels(GameController gameController)
    {
        try
        {
            return gameController.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?
                .Where(x => x != null && x.Label is { IsValid: true } && x.ItemOnGround != null &&
                            x.ItemOnGround.Type == EntityType.WorldItem && x.ItemOnGround.IsTargetable &&
                            x.ItemOnGround.HasComponent<WorldItem>() &&
                            IsQuestItem(gameController, x.ItemOnGround))
                .ToList() ?? new List<LabelOnGround>();
        }
        catch
        {
            return new List<LabelOnGround>();
        }
    }

    private static bool IsQuestItem(GameController gameController, Entity itemOnGround)
    {
        try
        {
            var itemEntity = itemOnGround.GetComponent<WorldItem>()?.ItemEntity;
            if (itemEntity == null) return false;
            return gameController.Files.BaseItemTypes.Translate(itemEntity.Path)?.ClassName == "QuestItem";
        }
        catch
        {
            return false;
        }
    }
}
