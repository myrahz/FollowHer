using System;
using ExileCore.PoEMemory.MemoryObjects;
using System.Numerics;
using FollowHer.Features.Targeting.EntityInformation;

namespace FollowHer.Core.Events.Events
{
    public class TargetEvent
    {
        public Entity Entity { get; }
        public TargetEventType EventType { get; }
        public DateTime Timestamp { get; }

        public TargetEvent(Entity entity, TargetEventType eventType)
        {
            Entity = entity;
            EventType = eventType;
            Timestamp = DateTime.UtcNow;
        }
    }

    public class TargetAcquiredEvent : TargetEvent
    {
        public float Distance { get; }
        public Vector2 Position { get; }
        public float Weight { get; }

        public TargetAcquiredEvent(
            Entity entity,
            float distance,
            Vector2 position,
            float weight)
            : base(entity, TargetEventType.Acquired)
        {
            Distance = distance;
            Position = position;
            Weight = weight;
        }
    }

    public class TargetLostEvent : TargetEvent
    {
        public string Reason { get; }
        public Vector2 LastKnownPosition { get; }

        public TargetLostEvent(
            Entity entity,
            string reason,
            Vector2 lastKnownPosition)
            : base(entity, TargetEventType.Lost)
        {
            Reason = reason;
            LastKnownPosition = lastKnownPosition;
        }
    }

    public class TargetStateChangedEvent : TargetEvent
    {
        public bool IsAlive { get; }
        public bool IsTargetable { get; }
        public float HealthPercentage { get; }
        public float Distance { get; }

        public TargetStateChangedEvent(
            Entity entity,
            bool isAlive,
            bool isTargetable,
            float healthPercentage,
            float distance)
            : base(entity, TargetEventType.StateChanged)
        {
            IsAlive = isAlive;
            IsTargetable = isTargetable;
            HealthPercentage = healthPercentage;
            Distance = distance;
        }
    }

    public class TargetPriorityChangedEvent : TargetEvent
    {
        public float OldWeight { get; }
        public float NewWeight { get; }
        public float Distance { get; }

        public TargetPriorityChangedEvent(
            Entity entity,
            float oldWeight,
            float newWeight,
            float distance)
            : base(entity, TargetEventType.PriorityChanged)
        {
            OldWeight = oldWeight;
            NewWeight = newWeight;
            Distance = distance;
        }
    }

    public class TargetOutOfRangeEvent : TargetEvent
    {
        public float CurrentDistance { get; }
        public float MaxAllowedDistance { get; }

        public TargetOutOfRangeEvent(
            Entity entity,
            float currentDistance,
            float maxAllowedDistance)
            : base(entity, TargetEventType.OutOfRange)
        {
            CurrentDistance = currentDistance;
            MaxAllowedDistance = maxAllowedDistance;
        }
    }

    public class TargetInLineOfSightEvent : TargetEvent
    {
        public bool IsVisible { get; }
        public Vector2 Position { get; }
        public float Distance { get; }

        public TargetInLineOfSightEvent(
            Entity entity,
            bool isVisible,
            Vector2 position,
            float distance)
            : base(entity, TargetEventType.LineOfSightChanged)
        {
            IsVisible = isVisible;
            Position = position;
            Distance = distance;
        }
    }

    public class TargetChangedEvent
    {
        public EntityInfo OldTarget { get; set; }
        public EntityInfo NewTarget { get; set; }
    }
    public enum TargetEventType
    {
        Acquired,
        Lost,
        StateChanged,
        PriorityChanged,
        OutOfRange,
        LineOfSightChanged
    }
}