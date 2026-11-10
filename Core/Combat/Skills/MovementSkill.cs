using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;

namespace FollowHer.Core.Combat.Skills
{
    /// <summary>A movement skill. Every setting that depends on the skill - how far it carries you, how
    /// close to a wall it will still fire, whether it is any use over a gap - lives here rather than in
    /// the global movement settings, because Leap Slam, Frostblink and Shield Charge differ in all of it.</summary>
    public class MovementSkill : SkillBase
    {
        [Menu("Travels Through Obstacles", "Whether this skill passes over obstacles (blink/teleport: Frostblink, Flame Dash, Lightning Warp, Blink Arrow) rather than colliding with them (ground dash: Shield Charge, Whirling Blades, Leap Slam). Blinks are only fired when the way is blocked but visible; ground dashes need a clear corridor.")]
        public ToggleNode TravelsThroughObstacles { get; set; } = new(false);

        [Menu("Always Travels Full Distance", "The skill moves you the whole Travel Distance in the direction you aim, however close to the character you click (Frostblink: clicking 1 unit away still carries you the full distance). Skills that instead stop at the cursor (Leap Slam) or refuse to move below a minimum (Shield Charge) leave this off. It changes how jumps are planned - a full-distance skill can only land exactly one travel distance away - and means the cursor only has to give a direction.")]
        public ToggleNode TravelsFullDistance { get; set; } = new(false);

        [Menu("Use To Cross Blocked Paths", "Blink skills only. When the walkable way to the target is blocked or much longer than the straight line, but you can see across (a gap or chasm), blink over it instead of walking round. Replaces the old global Dash Enabled.")]
        public ToggleNode UseToCrossBlockedPaths { get; set; } = new(false);

        [Menu("Use For Travel", "Ground-dash skills only. Use this skill instead of walking when the path ahead is clear, aimed one travel distance ahead along the path. Never used to punch through something blocked. Replaces the old global Prefer Movement Skills For Travel.")]
        public ToggleNode UseForTravel { get; set; } = new(false);

        [Menu("Travel Distance Per Use", "How far one use of this skill carries you, in world units (Leap Slam 660, Shield Charge about 800). While walking a path the cursor is aimed this far ahead along it instead of at the next waypoint, so each use covers a full hop and only that hop has to be clear - a bend further along no longer blocks it. Blinks and jumps never aim beyond it. 0 = use Max Jump Distance, and if that is 0 too, aim at the waypoint.")]
        public RangeNode<int> HopDistance { get; set; } = new(0, 0, 2000);

        [Menu("Minimum Distance Per Use", "Don't use this skill to cover less than this many world units - the character walks instead. Measure it in game: do the shortest use of the skill and compare your position before and after (Shield Charge about 260). A skill is only faster than walking above roughly walk speed x its duration.")]
        public RangeNode<int> MinDistance { get; set; } = new(0, 0, 2000);

        [Menu("Hitbox Clearance (cells)", "Simulates this skill refusing to fire when the character is too close to an obstacle, as if the hitbox were bigger. Before using it, every cell within this radius of the character and of the whole hop must be walkable - including where you stand now, so hugging a wall blocks it until you've moved clear. 1 cell is about 11 world units; cell values count 1..5 with distance from a wall, so a radius of 5 means 'open floor only'. 0 = only the cells on the line itself. Applies to skills that collide with obstacles, not blinks. Wall Clearance should be at least this large or the route never leaves enough room.")]
        public RangeNode<float> ClearanceRadius { get; set; } = new(0f, 0f, 8f);

        [Menu("Max Jump Distance", "How far this skill can jump over a gap or low obstacle, in world units, measured from where you stand. Pathfinding uses it to plan shortcuts over gaps. 0 = never plan jumps with this skill - leave it 0 on skills that collide with walls (Shield Charge), or the planner will send them over obstacles they cannot cross. Set it to what you actually see in game: too low and shortcuts are missed, too high and the character falls short.")]
        public RangeNode<int> MaxJumpDistance { get; set; } = new(0, 0, 2000);

        [Menu("Jump Path Cost (cells)", "How much walking a jump with this skill is worth to the path planner, in grid cells. A jump is only planned when the walkable detour is longer than the jump plus this. Raise it for a skill with a long cooldown, lower it for one you can spam.")]
        public RangeNode<int> JumpPathCost { get; set; } = new(20, 0, 100);

        [Menu("Wait For Cooldown (ms)", "A planned jump needs this skill, and it may be on cooldown by the time the character reaches the takeoff. Wait up to this long for it; after that give up on the jump and path around it instead. 0 = never wait.")]
        public RangeNode<int> CooldownWaitMs { get; set; } = new(2500, 0, 10000);

        [Menu("Shortcut Path Ratio", "Blink skills only. When line of sight to the leader is blocked, blink straight to them instead of pathfinding if the walkable route is at least this many times longer than the straight line. 1 = always blink when blocked; higher = only to skip a big detour.")]
        public RangeNode<float> ShortcutPathRatio { get; set; } = new(1.5f, 1f, 5f);

        /// <summary>The distance one use is expected to cover: the explicit travel distance, else the
        /// jump range, else unknown (0).</summary>
        [JsonIgnore]
        public int EffectiveHopDistance => HopDistance.Value > 0 ? HopDistance.Value : MaxJumpDistance.Value;

        // Which generation of per-skill jump defaults has been seeded into this skill. Without it a
        // skill saved before the jump settings existed would stay at 0 forever, and a user who
        // deliberately set 0 would have it overwritten - this tells the two apart. It is a version
        // rather than a flag so skills added to the defaults later can still be seeded on a skill that
        // was saved under the earlier set.
        public int JumpDefaultsVersion { get; set; }

        // Superseded by JumpDefaultsVersion; still read so settings saved with it migrate as version 1.
        public bool JumpDefaultsApplied { get; set; }

        // Same idea for the settings that used to be global (Dash Enabled, Prefer Movement Skills,
        // Min/Max Distance, Clearance Margin, Dash Shortcut Ratio, Jump Path Cost): 0 = still holds
        // the defaults, 1 = seeded once from those old values and the known per-skill defaults.
        public int MovementSettingsVersion { get; set; }
    }
}
