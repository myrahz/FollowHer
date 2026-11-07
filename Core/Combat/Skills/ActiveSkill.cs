using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using System;
using System.Windows.Forms;

namespace FollowHer.Core.Combat.Skills
{
    public class ActiveSkill
    {
        [JsonIgnore]
        public ActorSkill Skill;

        public string Name { get; set; } = "";
        public int Id { get; set; } = -1;
        [JsonIgnore]
        public int InternalId { get; set; } = -1;
        public HotkeyNode Key { get; set; } = new(Keys.None);
        public ToggleNode Enabled { get; set; } = new(false);
        [JsonIgnore]
        public bool CanUse => Skill != null &&
                             Skill.CanBeUsed &&
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

        public ListNode LineOfSightType { get; set; } = new ListNode();

        [Menu("Travels Through Obstacles", "Whether this skill ignores physical obstacles in its path (blink/teleport, e.g. Frostblink/FlameDash/LightningWarp/BlinkArrow) rather than colliding with them (ground dash, e.g. Shield Charge/Whirling Blades)")]
        public ToggleNode TravelsThroughObstacles { get; set; } = new(false);

        [Menu("Max Jump Distance", "How far this skill carries you in one use, in world units, measured from where you stand. Pathfinding uses it to look for jumps over gaps and low obstacles (Leap Slam, Flame Dash, Frostblink...). 0 = never plan jumps with this skill. The real range depends on your gems and gear, so set it to what you actually see in game: too low and shortcuts are missed, too high and the character falls short of the landing and gives up on that jump after a few seconds.")]
        public RangeNode<int> MaxJumpDistance { get; set; } = new(0, 0, 2000);

        // Which generation of per-skill jump defaults has been seeded into this skill. Without it a
        // skill saved before the jump settings existed would stay at 0 forever, and a user who
        // deliberately set 0 would have it overwritten - this tells the two apart. It is a version
        // rather than a flag so skills added to the defaults later (Blink Arrow, Lightning Warp) can
        // still be seeded on a skill that was saved under the earlier set.
        public int JumpDefaultsVersion { get; set; }

        // Superseded by JumpDefaultsVersion; still read so settings saved with it migrate as version 1.
        public bool JumpDefaultsApplied { get; set; }

        [Menu("Requires Aiming", "Whether this skill needs the cursor placed on the target before casting - uncheck for buffs/supports/cries that fire immediately without targeting")]
        public ToggleNode RequiresAiming { get; set; } = new(true);

        public override string ToString() => $"{Name}";
    }
}

