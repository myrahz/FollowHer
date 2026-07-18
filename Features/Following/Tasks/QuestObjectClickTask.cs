using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace FollowHer.Features.Following.Tasks;

/// <summary>Walks to and clicks nearby EntityType.IngameIcon world objects (levers, quest
/// triggers, etc.) within range - same shape as QuestItemPickupTask, but for world entities
/// rather than ground-item labels.</summary>
public class QuestObjectClickTask : IFollowTask
{
    private const float ApproachTolerance = 40f;
    private const int MaxClickAttempts = 3;
    private const int ClickCooldownMs = 1000;

    private long _currentObjectAddress;
    private int _attemptCount;
    private DateTime _nextClickAt = DateTime.MinValue;

    public bool TryExecute(FollowTaskContext context)
    {
        if (!FollowHer.Instance.Settings.Combat.Follow.Tasks.ClickQuestObjects) return false;

        var questObject = FindNearestQuestObject(context.GameController, context.Player.PosNum);
        if (questObject == null)
        {
            _currentObjectAddress = 0;
            _attemptCount = 0;
            return false;
        }

        var clickRange = FollowHer.Instance.Settings.Combat.Follow.Tasks.QuestObjectClickRange.Value;
        var distance = Vector3.Distance(context.Player.PosNum, questObject.PosNum);
        if (distance >= clickRange) return false;

        if (_currentObjectAddress != questObject.Address)
        {
            _currentObjectAddress = questObject.Address;
            _attemptCount = 0;
            _nextClickAt = DateTime.MinValue;
        }

        if (_attemptCount >= MaxClickAttempts)
        {
            context.Manager.LogDebug("Gave up clicking quest object - too many attempts");
            return false;
        }

        if (distance > ApproachTolerance)
        {
            return context.Manager.ExecuteMovement(questObject.PosNum, questObject.GridPosNum);
        }

        if (DateTime.Now < _nextClickAt) return true;

        context.Manager.LogDebug($"Clicking quest object '{questObject.Metadata}'");

        var label = FindGroundLabel(context.GameController, questObject);
        if (label != null)
        {
            context.Manager.ClickElement(label.Label);
        }
        else
        {
            context.Manager.ClickWorldPosition(questObject.PosNum);
        }

        _attemptCount++;
        _nextClickAt = DateTime.Now.AddMilliseconds(ClickCooldownMs);
        return true;
    }

    public void Reset()
    {
        _currentObjectAddress = 0;
        _attemptCount = 0;
        _nextClickAt = DateTime.MinValue;
    }

    private static Entity FindNearestQuestObject(GameController gameController, Vector3 playerPos)
    {
        try
        {
            return gameController.EntityListWrapper.ValidEntitiesByType.GetValueOrDefault(EntityType.IngameIcon)?
                .Where(x => x is { IsValid: true, IsTargetable: true })
                .OrderBy(x => Vector3.Distance(playerPos, x.PosNum))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static LabelOnGround FindGroundLabel(GameController gameController, Entity entity)
    {
        return gameController.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?
            .FirstOrDefault(x => x.IsVisible && x.ItemOnGround?.Address == entity.Address);
    }
}
