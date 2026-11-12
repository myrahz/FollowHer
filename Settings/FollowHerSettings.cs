using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using FollowHer.Core.Combat;
using FollowHer.Core.Combat.Rules;
using FollowHer.Core.Combat.Skills;
using Newtonsoft.Json;

namespace FollowHer.Settings;

/// <summary>The two ways the plugin runs. Exactly one is active at a time; switching stops whatever
/// the other was doing.</summary>
public enum PluginMode
{
    /// <summary>Follow a leader character, and fight when they fight.</summary>
    Follow,

    /// <summary>Walk to a target picked in the navigator window, and fight while the fight key is held.</summary>
    Navigator,
}

public class FollowHerSettings : ISettings
{
    public const string FollowModeName = "Follow";
    public const string NavigatorModeName = "Navigator";

    public ToggleNode Enable { get; set; } = new(false);

    [Menu("Mode", "Follow: follows the character named below and fights when they fight. Navigator: walks to a target picked in the Target Navigator window - holds Move Key to walk (or toggles it) and fights while the Precision key is held. Only one mode runs at a time; switching stops the other.")]
    public ListNode Mode { get; set; } = new() { Value = FollowModeName };

    [JsonIgnore]
    public PluginMode CurrentMode => Mode.Value == NavigatorModeName ? PluginMode.Navigator : PluginMode.Follow;

    [Menu("Leader Name", "Follow mode only: the exact in-game name of the character to follow")]
    public TextNode LeaderName { get; set; } = new("");

    public HotkeySettings Hotkeys { get; set; } = new();
    public PartySettings Party { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    public MovementSettings Movement { get; set; } = new();
    public NavigationSettings Navigation { get; set; } = new();
    public CombatSettings Combat { get; set; } = new();
    public TargetingSettings Targeting { get; set; } = new();
    public RenderSettings Render { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class HotkeySettings
{
    public HotkeyNode PrecisionKey { get; set; } = new(Keys.None);
    public HotkeyNode PrecisionToggleKey { get; set; } = new(Keys.None);

    [Menu("Movement Toggle Key", "Toggles following the leader on/off, independent of the Movement > Enable setting")]
    public HotkeyNode MovementToggleKey { get; set; } = new(Keys.None);

    [Menu("Fighting Toggle Key", "Toggles attacking on/off, in addition to the Precision toggle key/hotkey")]
    public HotkeyNode FightingToggleKey { get; set; } = new(Keys.None);
}

[Submenu(CollapsedByDefault = true)]
public class PartySettings
{
    [Menu("Auto Accept Leader Party Invite", "Automatically accept a party invite when the inviter's name matches Leader Name - party invites from anyone else, and every non-party invite (trade/friend/guild), are left alone")]
    public ToggleNode AutoAcceptLeaderInvite { get; set; } = new(false);

    [Menu("Accept Delay (ms)", "Wait this long after the leader's invite first appears before clicking Accept, rather than clicking on the frame it renders")]
    public RangeNode<int> AcceptDelay { get; set; } = new(500, 0, 5000);
}

[Submenu(CollapsedByDefault = true)]
public class AutomationSettings
{
    [Menu("Auto Level Up Gems", "When the gem level-up panel appears, click its 'Level Up All Gems' button. Follow mode only (Navigator mode is assisted, so it never clicks for you). Runs every tick regardless of the movement/fighting toggles.")]
    public ToggleNode AutoLevelUpGems { get; set; } = new(false);
}

[Submenu(CollapsedByDefault = true)]
public class MovementSettings
{
    public ToggleNode Enable { get; set; } = new(true);
    public RangeNode<int> KeepWithinDistance { get; set; } = new(200, 10, 1000);
    public RangeNode<int> TransitionDistance { get; set; } = new(500, 100, 5000);

    [Menu("Zone Update Buffer (ms)")]
    public RangeNode<int> ZoneUpdateBuffer { get; set; } = new(1000, 500, 5000);

    [Menu("Zone Transition Max Walk Distance", "Prefer walking to and clicking the matching zone transition over teleporting to the leader - only teleport if the transition is farther than this or isn't visible at all")]
    public RangeNode<int> ZoneTransitionMaxWalkDistance { get; set; } = new(300, 50, 2000);

    [Menu("Portal Approach Distance", "How close to get to a transition/portal before clicking it - doesn't need to be the portal's exact position, which may not be walkable")]
    public RangeNode<int> PortalApproachDistance { get; set; } = new(80, 20, 300);

    [Menu("Transition Search Radius", "How far from where the leader was last seen to look for the transition/portal they went through. Raise this if the follower reaches the leader's last spot but never finds the transition (e.g. the leader rounded a corner before going through).")]
    public RangeNode<int> TransitionSearchRadius { get; set; } = new(200, 50, 1000);

    [Menu("Leader Teleport Jump Distance", "If the leader's position jumps more than this (world units) in a single update - e.g. you're right next to them and suddenly they read across the map - treat it as them having taken a transition at their PREVIOUS position rather than chasing the far reading. Set 0 to disable. Must be large enough to not trip on a normal movement-skill hop.")]
    public RangeNode<int> LeaderTeleportJumpDistance { get; set; } = new(1500, 0, 10000);

    public ToggleNode CloseFollow { get; set; } = new(true);

    [Menu("Disable Movement In Town", "Don't follow the leader while standing in a town or hideout")]
    public ToggleNode DisableMovementInTown { get; set; } = new(false);

    [Menu("Max Waypoint Distance", "How far apart path waypoints may be placed (world units) where the way ahead is clear. Corners and turns keep tight waypoints on their own - the line of sight breaks there - so raising this only stretches the straight sections, which is what gives a movement skill a hop long enough to be worth using. Note it can't produce a movement target closer than Keep Within Distance x 1.5, since waypoints inside that are treated as already reached.")]
    public RangeNode<int> MaxWaypointDistance { get; set; } = new(400, 50, 2000);

    [Menu("Wall Clearance", "How many grid cells the path tries to stay away from walls and obstacles (about 11 world units each). Routes that hug walls leave movement skills no room - ground dashes need a clear corridor. This is a soft preference: a narrower gap is still used when it's the only way through. 0 = plain shortest path.")]
    public RangeNode<int> WallClearance { get; set; } = new(3, 0, 8);

    [Menu("Path Straightness", "How strongly the route prefers long straight runs over bends. Plain shortest paths wiggle - many routes of the same length differ only in how often they turn - and every bend shortens the stretch a movement skill can fly along. Higher = fewer, longer straight segments (and the route will detour a little to avoid a turn); lower = hugs the shortest path and bends freely. 0 = off. Works alongside Wall Clearance: both are soft preferences that the route trades off.")]
    public RangeNode<int> PathStraightness { get; set; } = new(4, 0, 10);

    [Menu("Plan Jumps In Pathfinding", "Let the path use movement skills that jump over gaps and low obstacles (Leap Slam, Flame Dash, Frostblink) as a shortcut when walking around is much longer. Each skill's reach is its Max Jump Distance and its cost is its Jump Path Cost, both in the Movement Skills list; a skill with 0 Max Jump Distance is never used for this.")]
    public ToggleNode EnableJumpPathing { get; set; } = new(true);

    [Menu("Open Doors On The Route", "Plan routes through closed doors the way Radar does (a closed door is treated as passable) and click one open only when the route actually runs through it. Stays off levers, labyrinth doors and NPC doors. Off = closed doors are walls, as in the raw terrain data.")]
    public ToggleNode OpenDoors { get; set; } = new(true);

    [Menu("Door Open Distance", "How close to a door that blocks the route to be before clicking it, in world units.")]
    public RangeNode<int> DoorOpenDistance { get; set; } = new(150, 50, 300);

    [Menu("Input Frequency (ms)", "Minimum delay between successive movement inputs")]
    public RangeNode<int> InputFrequency { get; set; } = new(50, 1, 100);

    // The settings below used to be global and are now per movement skill. They are only read so a
    // saved profile can seed each skill once (SkillHandler.ApplyMovementSettingsMigration); nothing
    // writes or shows them. Plain fields, so the settings auto-renderer skips them; the JSON names are
    // the old ones, so existing files load into them.
    [JsonProperty("DashEnabled")] public LegacyNode<bool> LegacyDashEnabled;
    [JsonProperty("PreferMovementSkillsForTravel")] public LegacyNode<bool> LegacyPreferMovementSkillsForTravel;
    [JsonProperty("MovementSkillClearanceMargin")] public LegacyNode<float> LegacyClearanceMargin;
    [JsonProperty("DashShortcutPathRatio")] public LegacyNode<float> LegacyDashShortcutPathRatio;
    [JsonProperty("MovementSkillMinDistance")] public LegacyNode<int> LegacyMinDistance;
    [JsonProperty("JumpPathCost")] public LegacyNode<int> LegacyJumpPathCost;

    /// <summary>The <c>{"Value": x}</c> shape every settings node is saved as.</summary>
    public class LegacyNode<T>
    {
        public T Value { get; set; }
    }

    public ToggleNode EnablePathfindingFallback { get; set; } = new(true);

    public ContentNode<MovementSkill> MovementSkills { get; set; } = new ContentNode<MovementSkill>()
    {
        EnableItemCollapsing = true,
        EnableControls = false,
    };

    // Settings of movement skills that were on the skill bar earlier but aren't now (the other weapon
    // set, a skill swapped out for a while). The list above only ever shows what's on the bar, and is
    // rebuilt from it, so without somewhere to keep these a skill's settings were lost the moment it
    // left the bar. A plain field so the settings auto-renderer skips it, like Combat.Profiles.
    public Dictionary<string, MovementSkill> MovementSkillArchive = new();

    public FollowVisualSettings Visual { get; set; } = new();
    public FollowDebugSettings Debug { get; set; } = new();
    public FollowTaskSettings Tasks { get; set; } = new();

    [Submenu(CollapsedByDefault = false)]
    public class FollowVisualSettings
    {
        public ToggleNode ShowFollowPath { get; set; } = new(true);
        public ColorNode TaskLineColor { get; set; } = new ColorNode((uint)Color.FromArgb(200, 0, 200, 255).ToArgb());
        public RangeNode<float> TaskLineWidth { get; set; } = new(2f, 1f, 5f);
    }

    [Submenu(CollapsedByDefault = true)]
    public class FollowDebugSettings
    {
        public ToggleNode ShowDetailedDebug { get; set; } = new(false);
    }

    [Submenu(CollapsedByDefault = true)]
    public class FollowTaskSettings
    {
        [Menu("Leader Proximity Range", "The leader must be within this distance of a quest item/object for either task to trigger")]
        public RangeNode<int> LeaderProximityRange { get; set; } = new(300, 50, 2000);

        [Menu("Pick Up Quest Items", "Walk to and pick up nearby quest items while following")]
        public ToggleNode PickUpQuestItems { get; set; } = new(true);

        [Menu("Quest Item Pickup Range", "Maximum distance to a quest item before walking to pick it up")]
        public RangeNode<int> QuestItemPickupRange { get; set; } = new(500, 50, 2000);

        [Menu("Click Quest Objects", "Walk to and click nearby quest objects (levers, triggers, etc.) while following")]
        public ToggleNode ClickQuestObjects { get; set; } = new(true);

        [Menu("Quest Object Click Range", "Maximum distance to a quest object before walking to click it")]
        public RangeNode<int> QuestObjectClickRange { get; set; } = new(300, 50, 2000);
    }
}

[Submenu(CollapsedByDefault = true)]
public class NavigationSettings
{
    [Menu("Show Target Navigator", "Show the overlay window listing this area's Radar targets, each with a button that walks you there using the follow pathfinding")]
    public ToggleNode ShowWindow { get; set; } = new(false);

    [Menu("Target Navigator Toggle Key", "Shows/hides the target navigator window")]
    public HotkeyNode ToggleWindowKey { get; set; } = new(Keys.None);

    [Menu("Move Key", "Navigator mode: walks to the target last clicked in the navigator window. How it behaves is set by Move Key Behavior. Fighting is separate - hold the Precision key (Hotkeys) to attack; when it isn't held the character moves.")]
    public HotkeyNode MoveKey { get; set; } = new(Keys.None);

    public const string MoveToggleName = "Toggle";
    public const string MoveHoldName = "Hold";

    [Menu("Move Key Behavior", "Toggle: press once to start walking to the selected target, press again to stop. Hold: walk only while the key is held; releasing stops (the target stays selected).")]
    public ListNode MoveKeyBehavior { get; set; } = new() { Value = MoveToggleName };

    [JsonIgnore]
    public bool MoveKeyIsHold => MoveKeyBehavior.Value == MoveHoldName;

    [Menu("Stop Navigation Key", "Stops walking to the current target. The target is remembered, so Resume picks it back up.")]
    public HotkeyNode StopKey { get; set; } = new(Keys.None);

    [Menu("Resume Navigation Key", "Walks to the last picked target again, replanning from where you're standing. Forgotten on area change.")]
    public HotkeyNode ResumeKey { get; set; } = new(Keys.None);

    [Menu("Arrival Distance", "Stop when this close (world units) to the chosen target - targets sit on a tile cluster's center, which you often can't stand exactly on")]
    public RangeNode<int> ArrivalDistance { get; set; } = new(60, 10, 500);

    [Menu("Navigation Timeout (s)", "Give up on a destination after this long. 0 = never give up.")]
    public RangeNode<int> Timeout { get; set; } = new(180, 0, 900);

    [Menu("Radar targets.json Path", "Leave blank to auto-detect it under Plugins/Temp/Radar, then Plugins/Compiled/Radar, then Plugins/Source/Radar. Set it only if your Radar install lives somewhere else.")]
    public TextNode RadarTargetsPath { get; set; } = new("");
}

[Submenu(CollapsedByDefault = true)]
public class CombatSettings
{
    public ToggleNode AttackWhenLeaderIsAttacking { get; set; } = new(true);
    public RangeNode<int> DistanceToLeaderToAttack { get; set; } = new(40, 20, 200);

    [Menu("Attack When Leader Is Idle Near Monsters", "Also attack when the leader is standing still with hostile monsters close to them, even if they aren't attacking. Works alongside 'Attack When Leader Is Attacking' (either one opens the gate) and uses the same 'Distance To Leader To Attack' range.")]
    public ToggleNode AttackWhenLeaderIdleNearMonsters { get; set; } = new(false);

    [Menu("Idle Monster Radius", "How close (world units) a hostile monster must be to the idle leader to count.")]
    public RangeNode<int> IdleMonsterRadius { get; set; } = new(80, 20, 300);

    [Menu("Attack Grace Period (ms)", "Keep attacking for this long after the leader was last seen attacking, instead of requiring them to be attacking at that exact instant - smooths over the gaps between individual swings")]
    public RangeNode<int> AttackGracePeriod { get; set; } = new(500, 0, 3000);

    [Menu("Log Why Not Attacking", "Log the reason the combat engine produced no action - unresolved rule skill name, skill unusable, no valid targets, or no target matching the condition. Only logs when the reason changes.")]
    public ToggleNode LogWhyNotAttacking { get; set; } = new(false);

    // Skills that do NOT count as the leader attacking. Rendered by a custom ImGui editor
    // (CombatRuleEditor.DrawLeaderSkillBlacklist) rather than the settings auto-renderer, so it's a
    // plain field like Profiles below. Matching ignores case and whitespace and checks both the
    // skill's display and internal name, so one "Flame Dash" entry covers the internal "FlameDash".
    // Movement skills are the defaults: they report isAttacking and hold a skill slot like a real
    // attack, which is what the old hardcoded animation exclusion list existed to filter out.
    public List<LeaderSkillBlacklistEntry> LeaderSkillBlacklistEntries = BuildDefaultLeaderSkillBlacklist();

    private static List<LeaderSkillBlacklistEntry> BuildDefaultLeaderSkillBlacklist() => new()
    {
        new("Dash"),
        new("Flame Dash"),
        new("Frostblink"),
        new("Lightning Warp"),
        new("Leap Slam"),
        new("Shield Charge"),
        new("NewShieldCharge"),
        new("Whirling Blades"),
        new("Charged Dash"),
        new("Blink Arrow"),
        new("Mirror Arrow"),
        new("Withering Step"),
        new("Phase Run"),
        new("Smoke Mine"),
        new("Convocation"),
        new("Portal"),
    };

    public ContentNode<ActiveSkill> Skills { get; set; } = new ContentNode<ActiveSkill>()
    {
        EnableItemCollapsing = true,
        EnableControls = false,
    };

    // See MovementSettings.MovementSkillArchive: settings of combat skills that aren't on the bar
    // right now, kept so swapping weapon sets doesn't reset them.
    public Dictionary<string, ActiveSkill> SkillArchive = new();

    // Plain fields (not properties) so ExileCore's settings auto-renderer skips them entirely -
    // the rule/profile editor (Features/Rendering/CombatRuleEditor.cs) renders these itself via
    // FollowHer.DrawSettings(), the same technique ReAgent uses for its own rule data.
    public Dictionary<string, CombatRuleProfile> Profiles = new();
    public string ActiveProfile = "";

    // Profiles added to the defaults after first run are seeded once into existing installs; this
    // remembers that so deleting one doesn't bring it back on the next load.
    public bool SeededStaticStrikeHelix;
    public bool SeededSlamsAndRfV2;
}

[Submenu(CollapsedByDefault = true)]
public class TargetingSettings
{
    public RangeNode<int> ScanRadius { get; set; } = new(100, 1, 500);
    public RangeNode<float> MaxTargetRange { get; set; } = new(100f, 0f, 200f);
    public ToggleNode PrioritizeCurrentTarget { get; set; } = new(true);

    [Menu("Target Switch Cooldown (s)", "Minimum time between target switches")]
    public RangeNode<float> TargetSwitchThreshold { get; set; } = new(0.0f, 0.0f, 3.0f);

    [Menu("Min Weight Difference To Switch", "Minimum weight advantage a new target must have over the current one to switch")]
    public RangeNode<float> MinWeightDifferenceForSwitch { get; set; } = new(0.5f, 0f, 5f);

    public PrioritySettings Priorities { get; set; } = new();
    public LineOfSightSettings LineOfSight { get; set; } = new();
    public DensitySettings Density { get; set; } = new();

    [Submenu(CollapsedByDefault = false)]
    public class LineOfSightSettings
    {
        public ToggleNode RequireLineOfSight { get; set; } = new(true);
    }

    [Submenu(CollapsedByDefault = false)]
    public class PrioritySettings
    {
        public RangeNode<float> DistanceWeight { get; set; } = new(1.0f, 0f, 5f);

        public HealthSettings Health { get; set; } = new();
        public RaritySettings Rarity { get; set; } = new();

        [Submenu(CollapsedByDefault = false)]
        public class HealthSettings
        {
            public ToggleNode PreferHigherHealth { get; set; } = new(false);
            public RangeNode<float> HealthWeight { get; set; } = new(1.0f, 0f, 5f);
        }

        [Submenu(CollapsedByDefault = false)]
        public class RaritySettings
        {
            public ToggleNode ConsiderRarity { get; set; } = new(true);
            public RangeNode<float> NormalWeight { get; set; } = new(1.0f, 0f, 10f);
            public RangeNode<float> MagicWeight { get; set; } = new(2.0f, 0f, 10f);
            public RangeNode<float> RareWeight { get; set; } = new(3.0f, 0f, 10f);
            public RangeNode<float> UniqueWeight { get; set; } = new(4.0f, 0f, 10f);
        }
    }

    [Submenu(CollapsedByDefault = true)]
    public class DensitySettings
    {
        [Menu("Enable Clustering", "Enable density-based target selection")]
        public ToggleNode EnableClustering { get; set; } = new(true);

        [Menu("Cluster Radius", "Maximum radius for monster clusters")]
        public RangeNode<float> ClusterRadius { get; set; } = new(25f, 10f, 100f);

        [Menu("Minimum Cluster Size", "Minimum number of monsters to form a cluster")]
        public RangeNode<int> MinClusterSize { get; set; } = new(3, 2, 10);

        [Menu("Base Cluster Bonus", "Base weight multiplier for monsters in clusters")]
        public RangeNode<float> BaseClusterBonus { get; set; } = new(0.1f, 0f, 1f);

        [Menu("Maximum Cluster Bonus", "Maximum weight multiplier for dense clusters")]
        public RangeNode<float> MaxClusterBonus { get; set; } = new(2.0f, 1f, 5f);

        [Menu("Enable Core Bonus", "Additional bonus for monsters near cluster centers")]
        public ToggleNode EnableCoreBonus { get; set; } = new(true);

        [Menu("Core Bonus Multiplier", "Weight multiplier for monsters in cluster cores")]
        public RangeNode<float> CoreBonusMultiplier { get; set; } = new(1.2f, 1f, 2f);

        [Menu("Core Radius Percent", "Size of the core area relative to cluster radius")]
        public RangeNode<float> CoreRadiusPercent { get; set; } = new(0.5f, 0.1f, 1f);

        [Menu("Enable Isolation Penalty", "Penalty for isolated monsters")]
        public ToggleNode EnableIsolationPenalty { get; set; } = new(true);

        [Menu("Isolation Penalty Multiplier", "Weight multiplier for isolated monsters")]
        public RangeNode<float> IsolationPenaltyMultiplier { get; set; } = new(0.8f, 0.1f, 1f);
    }
}

[Submenu(CollapsedByDefault = true)]
public class RenderSettings
{
    public ToggleNode EnableRendering { get; set; } = new(true);
    public ToggleNode ShowDebugInfo { get; set; } = new(false);
    public ToggleNode ShowTerrainDebug { get; set; } = new(false);
    public ToggleNode ShowWalkableDebug { get; set; } = new(false);

    public TargetVisualsSettings TargetVisuals { get; set; } = new();
    public UISettings Interface { get; set; } = new();

    [Submenu(CollapsedByDefault = false)]
    public class TargetVisualsSettings
    {
        public ToggleNode ShowTargetHighlight { get; set; } = new(true);
        public ColorNode TargetHighlightColor { get; set; } = new ColorNode((uint)Color.FromArgb(180, 255, 165, 0).ToArgb());
        public RangeNode<float> HighlightThickness { get; set; } = new(2f, 1f, 5f);
        public ToggleNode ShowTargetHealth { get; set; } = new(true);
        public ColorNode HealthTextColor { get; set; } = new ColorNode((uint)Color.FromArgb(255, 255, 255, 255).ToArgb());
    }

    [Submenu(CollapsedByDefault = true)]
    public class UISettings
    {
        public ToggleNode EnableWithFullscreenUI { get; set; } = new(false);
        public ToggleNode EnableWithLeftPanel { get; set; } = new(false);
        public ToggleNode EnableWithRightPanel { get; set; } = new(false);
    }
}
