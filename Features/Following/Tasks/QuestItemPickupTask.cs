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

/// <summary>Ported from AreWeThereYet's AutoPilot.GetQuestItem()/Loot task - walks to and clicks
/// the ground label of the nearest QuestItem, giving up after a few failed attempts.
///
/// Once it commits to an item it stays committed until the item is gone, the attempt cap is hit,
/// or the commitment times out. The acquisition gates (pickup range, leader proximity) are checked
/// *only* before committing: re-checking them every tick meant that a leader who kept walking
/// would pull those gates false mid-approach, hand control back to follow, and then hand it back
/// to this task a moment later - the follower oscillated between the item and the leader and burnt
/// its attempt budget without ever reaching the item.</summary>
public class QuestItemPickupTask : IFollowTask
{
    private const float ApproachTolerance = 40f;
    private const int MaxClickAttempts = 3;
    private const int ClickCooldownMs = 1000;
    private const int CommitmentTimeoutMs = 15000;

    private long _currentItemAddress;
    private int _attemptCount;
    private DateTime _nextClickAt = DateTime.MinValue;
    private DateTime _commitmentExpiresAt = DateTime.MaxValue;

    // Items this task has already given up on. Without this, giving up just frees the task to
    // re-acquire the same item on the very next tick, which is its own infinite loop.
    private readonly HashSet<long> _abandonedItems = new();

    public bool TryExecute(FollowTaskContext context)
    {
        if (!FollowHer.Instance.Settings.Movement.Tasks.PickUpQuestItems) return false;
        if (context.GameController.Area?.CurrentArea?.IsHideout == true) return false;

        // Prefer the item already committed to, looked up by address - not whatever happens to be
        // nearest this tick, which can flip as the follower moves.
        var questItem = _currentItemAddress != 0
            ? FindQuestItemByAddress(context.GameController, _currentItemAddress)
            : null;

        if (questItem == null && _currentItemAddress != 0)
        {
            // Committed item is gone - picked up, or out of render range. Either way we're done.
            ClearCommitment();
        }

        if (questItem == null)
        {
            questItem = FindNearestQuestItem(context.GameController, context.Player.PosNum, _abandonedItems);
            if (questItem == null) return false;

            if (!PassesAcquisitionGates(context, questItem)) return false;

            _currentItemAddress = questItem.Address;
            _attemptCount = 0;
            _nextClickAt = DateTime.MinValue;
            _commitmentExpiresAt = DateTime.Now.AddMilliseconds(CommitmentTimeoutMs);
        }

        if (_attemptCount >= MaxClickAttempts)
        {
            context.Manager.LogDebug("Gave up picking up quest item - too many click attempts");
            Abandon();
            return false;
        }

        if (DateTime.Now > _commitmentExpiresAt)
        {
            context.Manager.LogDebug("Gave up picking up quest item - took too long to reach it");
            Abandon();
            return false;
        }

        var distance = Vector3.Distance(context.Player.PosNum, questItem.PosNum);
        if (distance > ApproachTolerance)
        {
            return context.Manager.ExecuteMovement(questItem.PosNum, questItem.GridPosNum);
        }

        // In range but the label hasn't rendered yet - hold the commitment rather than falling
        // through to follow, which would walk us back out of range again.
        var label = FindGroundLabel(context.GameController, questItem);
        if (label == null) return true;

        if (DateTime.Now < _nextClickAt) return true;

        context.Manager.LogDebug($"Picking up quest item '{questItem.Metadata}'");
        context.Manager.ClickElement(label.Label);
        _attemptCount++;
        _nextClickAt = DateTime.Now.AddMilliseconds(ClickCooldownMs);
        return true;
    }

    // Only consulted before committing to an item, never while pursuing one.
    private static bool PassesAcquisitionGates(FollowTaskContext context, Entity questItem)
    {
        var settings = FollowHer.Instance.Settings.Movement.Tasks;

        var distance = Vector3.Distance(context.Player.PosNum, questItem.PosNum);
        if (distance >= settings.QuestItemPickupRange.Value) return false;

        // Only chase this item if the leader is actually near it - otherwise the follower would
        // wander off toward stray quest items the leader has no intention of picking up.
        if (context.LeaderEntity == null) return false;
        return Vector3.Distance(context.LeaderEntity.PosNum, questItem.PosNum) <= settings.LeaderProximityRange.Value;
    }

    private void Abandon()
    {
        if (_currentItemAddress != 0) _abandonedItems.Add(_currentItemAddress);
        ClearCommitment();
    }

    private void ClearCommitment()
    {
        _currentItemAddress = 0;
        _attemptCount = 0;
        _nextClickAt = DateTime.MinValue;
        _commitmentExpiresAt = DateTime.MaxValue;
    }

    public void Reset()
    {
        ClearCommitment();
        _abandonedItems.Clear();
    }

    private static Entity FindNearestQuestItem(GameController gameController, Vector3 playerPos, HashSet<long> exclude)
    {
        try
        {
            return gameController.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?
                .Where(x => x != null && x.IsVisible && x.Label is { IsValid: true, IsVisible: true } &&
                            x.ItemOnGround != null && x.ItemOnGround.Type == EntityType.WorldItem &&
                            x.ItemOnGround.IsTargetable && x.ItemOnGround.HasComponent<WorldItem>() &&
                            !exclude.Contains(x.ItemOnGround.Address) &&
                            IsQuestItem(gameController, x.ItemOnGround))
                .Select(x => x.ItemOnGround)
                .OrderBy(x => Vector3.Distance(playerPos, x.PosNum))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static Entity FindQuestItemByAddress(GameController gameController, long address)
    {
        try
        {
            return gameController.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?
                .Where(x => x != null && x.ItemOnGround != null && x.ItemOnGround.Address == address &&
                            x.ItemOnGround.IsTargetable)
                .Select(x => x.ItemOnGround)
                .FirstOrDefault();
        }
        catch
        {
            return null;
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

    private static LabelOnGround FindGroundLabel(GameController gameController, Entity item)
    {
        return gameController.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?
            .FirstOrDefault(x => x.IsVisible && x.ItemOnGround?.Address == item.Address);
    }
}
