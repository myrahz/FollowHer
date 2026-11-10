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

        [Menu("Hitbox Clearance (cells)", "Simulates this skill refusing to fire when the character is too close to an obstacle, as if the hitbox were bigger. Before using the skill, every cell within this radius of the character and of the whole hop must be walkable - including where you are standing now, so hugging a wall or a corner blocks it until you've moved clear (the plugin simply walks on and tries again). 1 cell is about 11 world units. 0 = use the global Movement Skill Clearance Margin instead. Only applies to skills that collide with obstacles (ground dashes like Leap Slam / Shield Charge), not blinks. Wall Clearance should be at least this large, or the route never leaves enough room.")]
        public RangeNode<float> ClearanceRadius { get; set; } = new(0f, 0f, 8f);

        [Menu("Travel Distance Per Use", "How far one use of this movement skill carries you, in world units (e.g. Leap Slam 100). While walking a path the cursor is aimed this far ahead along it instead of at the next waypoint, so each use covers a full hop, and only that hop has to be clear of obstacles - a bend further along no longer blocks the skill. 0 = use Max Jump Distance, and if that is 0 too, aim at the waypoint as before.")]
        public RangeNode<int> HopDistance { get; set; } = new(0, 0, 2000);

        [Menu("Minimum Distance Per Use", "Don't use this skill to cover less than this many world units. The hop that would be made (the travel distance, or the distance left to the waypoint if that is shorter) has to be at least this long, otherwise the character walks instead. Measure it in game: do the shortest use of the skill and compare your position before and after (Shield Charge about 260). Adds to, never replaces, the global Movement Skill Min Distance - the larger of the two applies. 0 = no per-skill minimum.")]
        public RangeNode<int> MinDistance { get; set; } = new(0, 0, 2000);

        /// <summary>The distance one use is expected to cover: the explicit travel distance, else the
        /// jump range, else unknown (0).</summary>
        [JsonIgnore]
        public int EffectiveHopDistance => HopDistance.Value > 0 ? HopDistance.Value : MaxJumpDistance.Value;

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

