using System;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace FollowHer.Features.Following.Tasks;

/// <summary>Ported from AreWeThereYet's AutoPilot.GetQuestItem()/Loot task - walks to and clicks
/// the nearest ground-item labeled as a QuestItem, giving up after a few failed attempts.</summary>
public class QuestItemPickupTask : IFollowTask
{
    private const float ApproachTolerance = 40f;
    private const int MaxClickAttempts = 3;
    private const int ClickCooldownMs = 1000;

    private long _currentItemAddress;
    private int _attemptCount;
    private DateTime _nextClickAt = DateTime.MinValue;

    public bool TryExecute(FollowTaskContext context)
    {
        if (!FollowHer.Instance.Settings.Combat.Follow.Tasks.PickUpQuestItems) return false;
        if (context.GameController.Area?.CurrentArea?.IsHideout == true) return false;

        var questItem = FindNearestQuestItem(context.GameController, context.Player.PosNum);
        if (questItem == null)
        {
            _currentItemAddress = 0;
            _attemptCount = 0;
            return false;
        }

        var pickupRange = FollowHer.Instance.Settings.Combat.Follow.Tasks.QuestItemPickupRange.Value;
        var distance = Vector3.Distance(context.Player.PosNum, questItem.PosNum);
        if (distance >= pickupRange) return false;

        if (_currentItemAddress != questItem.Address)
        {
            _currentItemAddress = questItem.Address;
            _attemptCount = 0;
            _nextClickAt = DateTime.MinValue;
        }

        if (_attemptCount >= MaxClickAttempts)
        {
            context.Manager.LogDebug("Gave up picking up quest item - too many attempts");
            return false;
        }

        var label = FindGroundLabel(context.GameController, questItem);
        if (label == null) return false;

        if (distance > ApproachTolerance)
        {
            return context.Manager.ExecuteMovement(questItem.PosNum, questItem.GridPosNum);
        }

        if (DateTime.Now < _nextClickAt) return true;

        context.Manager.LogDebug($"Picking up quest item '{questItem.Metadata}'");
        context.Manager.ClickElement(label.Label);
        _attemptCount++;
        _nextClickAt = DateTime.Now.AddMilliseconds(ClickCooldownMs);
        return true;
    }

    public void Reset()
    {
        _currentItemAddress = 0;
        _attemptCount = 0;
        _nextClickAt = DateTime.MinValue;
    }

    private static Entity FindNearestQuestItem(GameController gameController, Vector3 playerPos)
    {
        try
        {
            return gameController.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?
                .Where(x => x != null && x.IsVisible && x.Label is { IsValid: true, IsVisible: true } &&
                            x.ItemOnGround != null && x.ItemOnGround.Type == EntityType.WorldItem &&
                            x.ItemOnGround.IsTargetable && x.ItemOnGround.HasComponent<WorldItem>() &&
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
