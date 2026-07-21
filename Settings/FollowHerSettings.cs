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

public class FollowHerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public TextNode LeaderName { get; set; } = new("");

    public HotkeySettings Hotkeys { get; set; } = new();
    public PartySettings Party { get; set; } = new();
    public MovementSettings Movement { get; set; } = new();
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

    public ToggleNode CloseFollow { get; set; } = new(true);

    [Menu("Disable Movement In Town", "Don't follow the leader while standing in a town or hideout")]
    public ToggleNode DisableMovementInTown { get; set; } = new(false);

    [Menu("Dash Enabled", "Use an enabled blink-type movement skill (travels through obstacles, e.g. Frostblink/FlameDash/LightningWarp/BlinkArrow) to punch through a blocked path instead of walking")]
    public ToggleNode DashEnabled { get; set; } = new(false);

    [Menu("Prefer Movement Skills For Travel", "Use an enabled ground-dash movement skill (collides with obstacles, e.g. Shield Charge/Whirling Blades) instead of walking when the path is already clear - never used to punch through something blocked")]
    public ToggleNode PreferMovementSkillsForTravel { get; set; } = new(false);

    [Menu("Movement Skill Clearance Margin", "Grid cells of clearance required on each side of a ground-dash skill's travel line before it's used (accounts for character hitbox width)")]
    public RangeNode<float> MovementSkillClearanceMargin { get; set; } = new(1.5f, 0.5f, 3f);

    [Menu("Dash Shortcut Path Ratio", "When line of sight to the leader is blocked, blink straight to them (needs Dash Enabled + an off-cooldown blink skill) instead of pathfinding, if the walkable route is at least this many times longer than the straight-line distance. 1 = always prefer blinking when blocked; higher = only blink to skip a big detour. The straight-line hop still obeys Movement Skill Min/Max Distance.")]
    public RangeNode<float> DashShortcutPathRatio { get; set; } = new(1.5f, 1f, 5f);

    [Menu("Movement Skill Min Distance", "Don't use a movement skill (Dash Enabled/Prefer Movement Skills For Travel) for a hop shorter than this")]
    public RangeNode<int> MovementSkillMinDistance { get; set; } = new(100, 0, 1000);

    [Menu("Movement Skill Max Distance", "Don't use a movement skill (Dash Enabled/Prefer Movement Skills For Travel) for a hop longer than this")]
    public RangeNode<int> MovementSkillMaxDistance { get; set; } = new(1000, 100, 3000);

    [Menu("Input Frequency (ms)", "Minimum delay between successive movement inputs")]
    public RangeNode<int> InputFrequency { get; set; } = new(50, 1, 100);

    public ToggleNode EnablePathfindingFallback { get; set; } = new(true);

    public ContentNode<ActiveSkill> MovementSkills { get; set; } = new ContentNode<ActiveSkill>()
    {
        EnableItemCollapsing = true,
        EnableControls = false,
    };

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
public class CombatSettings
{
    public ToggleNode AttackWhenLeaderIsAttacking { get; set; } = new(true);
    public RangeNode<int> DistanceToLeaderToAttack { get; set; } = new(40, 20, 200);

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

    // Plain fields (not properties) so ExileCore's settings auto-renderer skips them entirely -
    // the rule/profile editor (Features/Rendering/CombatRuleEditor.cs) renders these itself via
    // FollowHer.DrawSettings(), the same technique ReAgent uses for its own rule data.
    public Dictionary<string, CombatRuleProfile> Profiles = new();
    public string ActiveProfile = "";
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
        public ToggleNode EnablePriorities { get; set; } = new(true);
        public RangeNode<float> DistanceWeight { get; set; } = new(1.0f, 0f, 5f);

        public HealthSettings Health { get; set; } = new();
        public RaritySettings Rarity { get; set; } = new();

        [Submenu(CollapsedByDefault = false)]
        public class HealthSettings
        {
            public ToggleNode ConsiderHealth { get; set; } = new(true);
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
    public CursorSettings Cursor { get; set; } = new();

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

        public SafeZoneSettings SafeZone { get; set; } = new();

        [Submenu(CollapsedByDefault = true)]
        public class SafeZoneSettings
        {
            public RangeNode<float> LeftMargin { get; set; } = new(2f, 0f, 50f);
            public RangeNode<float> RightMargin { get; set; } = new(3f, 0f, 50f);
            public RangeNode<float> TopMargin { get; set; } = new(8f, 0f, 50f);
            public RangeNode<float> BottomMargin { get; set; } = new(15f, 0f, 50f);
        }
    }

    [Submenu(CollapsedByDefault = true)]
    public class CursorSettings
    {
        public RangeNode<int> TargetingRadius { get; set; } = new(10, 1, 100);
        public ToggleNode EnableRandomization { get; set; } = new(false);
        public RangeNode<float> RandomizationFactor { get; set; } = new(0.5f, 0.1f, 1.0f);
        public ToggleNode LimitCursorRange { get; set; } = new(false);
        public RangeNode<int> MaxCursorRange { get; set; } = new(300, 50, 1000);
    }
}
