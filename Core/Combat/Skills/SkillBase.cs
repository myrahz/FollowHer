using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using System;
using System.Windows.Forms;

namespace FollowHer.Core.Combat.Skills
{
    /// <summary>What every skill on the bar has, combat or movement: how it is found, pressed and paced.
    /// The two lists show different settings on top of this (see <see cref="ActiveSkill"/> and
    /// <see cref="MovementSkill"/>), so neither list is cluttered with fields that only mean something
    /// to the other.</summary>
    public abstract class SkillBase
    {
        [JsonIgnore]
        public ActorSkill Skill;

        public string Name { get; set; } = "";
        public int Id { get; set; } = -1;
        [JsonIgnore]
        public int InternalId { get; set; } = -1;
        public HotkeyNode Key { get; set; } = new(Keys.None);
        public ToggleNode Enabled { get; set; } = new(false);
        /// <summary>Whether the weapon set currently held can use this skill at all. Leap Slam on a skill
        /// bar shared by two weapon sets is the usual case: the game still reports CanBeUsed = true for it
        /// while it is greyed out, and pressing its key does nothing - so a skill that fails this must not
        /// be planned with, not just skipped at the moment of use.</summary>
        [JsonIgnore]
        public bool UsableWithCurrentWeapon => Skill != null && Skill.CanBeUsedWithWeapon;

        [JsonIgnore]
        public bool CanUse => Skill != null &&
                             Skill.CanBeUsed &&
                             Skill.CanBeUsedWithWeapon &&
                             Enabled;
        [JsonIgnore]
        public int Cooldown => 0;
        [JsonIgnore]
        public TimeSpan CastTime => Skill?.CastTime ?? TimeSpan.Zero;
        [JsonIgnore]
        public int TotalDelay => (int)(Cooldown + CastTime.TotalMilliseconds + ExtraDelay.Value);

        [Menu("Use Click Instead of Hold", "If enabled, skill will be activated with a click instead of being held down")]
        public ToggleNode UseClick { get; set; } = new ToggleNode(false);

        [Menu("Additional Delay (ms)", "Extra delay to add between skill uses")]
        public RangeNode<int> ExtraDelay { get; set; } = new(0, 0, 5000);

        public override string ToString() => $"{Name}";
    }
}
