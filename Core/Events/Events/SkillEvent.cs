using System;
using ExileCore.PoEMemory.MemoryObjects;

namespace FollowHer.Core.Events.Events
{
    public class SkillEvent
    {
        public ActorSkill Skill { get; }
        public SkillEventType EventType { get; }
        public DateTime Timestamp { get; }

        public SkillEvent(ActorSkill skill, SkillEventType eventType)
        {
            Skill = skill;
            EventType = eventType;
            Timestamp = DateTime.UtcNow;
        }
    }

    public class SkillUsedEvent : SkillEvent
    {
        public float CooldownRemaining { get; }
        public bool WasSuccessful { get; }
        public Entity Target { get; }

        public SkillUsedEvent(
            ActorSkill skill,
            float cooldownRemaining,
            bool wasSuccessful,
            Entity target = null)
            : base(skill, SkillEventType.Used)
        {
            CooldownRemaining = cooldownRemaining;
            WasSuccessful = wasSuccessful;
            Target = target;
        }
    }

    public class SkillCooldownEvent : SkillEvent
    {
        public float RemainingCooldown { get; }
        public bool IsReady { get; }

        public SkillCooldownEvent(
            ActorSkill skill,
            float remainingCooldown)
            : base(skill, SkillEventType.CooldownUpdate)
        {
            RemainingCooldown = remainingCooldown;
            IsReady = remainingCooldown <= 0;
        }
    }

    public class SkillStateChangedEvent : SkillEvent
    {
        public bool CanBeUsed { get; }
        public bool IsOnCooldown { get; }
        public bool HasRequiredResources { get; }

        public SkillStateChangedEvent(
            ActorSkill skill,
            bool canBeUsed,
            bool isOnCooldown,
            bool hasRequiredResources)
            : base(skill, SkillEventType.StateChanged)
        {
            CanBeUsed = canBeUsed;
            IsOnCooldown = isOnCooldown;
            HasRequiredResources = hasRequiredResources;
        }
    }

    public class SkillReadyEvent : SkillEvent
    {
        public SkillReadyEvent(ActorSkill skill)
            : base(skill, SkillEventType.Ready)
        {
        }
    }

    public class SkillInterruptedEvent : SkillEvent
    {
        public string Reason { get; }

        public SkillInterruptedEvent(
            ActorSkill skill,
            string reason)
            : base(skill, SkillEventType.Interrupted)
        {
            Reason = reason;
        }
    }

    public class SkillResourceEvent : SkillEvent
    {
        public float CurrentMana { get; }
        public float RequiredMana { get; }
        public bool HasEnoughResources { get; }

        public SkillResourceEvent(
            ActorSkill skill,
            float currentMana,
            float requiredMana)
            : base(skill, SkillEventType.ResourceUpdate)
        {
            CurrentMana = currentMana;
            RequiredMana = requiredMana;
            HasEnoughResources = currentMana >= requiredMana;
        }
    }

    public enum SkillEventType
    {
        Used,
        CooldownUpdate,
        StateChanged,
        Ready,
        Interrupted,
        ResourceUpdate
    }
}