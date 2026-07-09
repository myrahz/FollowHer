using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using FollowHer.Core.Combat.Skills;
using Newtonsoft.Json;

namespace FollowHer.Settings;

public class FollowHerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public ToggleNode UseTerrainTargeting { get; set; } = new(true);
    public TextNode LeaderName { get; set; } = new("");
    public ToggleNode AttackWhenLeaderIsAttacking { get; set; } = new(true);
    public RangeNode<int> DistanceToLeaderToAttack { get; set; } =  new(40, 20, 200);
    public HotkeyNode PrecisionKey { get; set; } = new(Keys.None);
    public HotkeyNode PrecisionToggleKey { get; set; } = new(Keys.None);
    public RenderSettings Render { get; set; } = new();
    public TargetingSettings Targeting { get; set; } = new();
    public CombatSettings Combat { get; set; } = new();
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

[Submenu(CollapsedByDefault = true)]
public class TargetingSettings
{
    public RangeNode<int> ScanRadius { get; set; } = new(100, 1, 500);
    public RangeNode<float> MaxTargetRange { get; set; } = new(100f, 0f, 200f);
    public ToggleNode PrioritizeCurrentTarget { get; set; } = new(true);
    public RangeNode<float> TargetSwitchThreshold { get; set; } = new(0.0f, 0.0f, 3.0f);

    public PrioritySettings Priorities { get; set; } = new();
    public LineOfSightSettings LineOfSight { get; set; } = new();
    public DensitySettings Density { get; set; } = new();

    [Submenu(CollapsedByDefault = false)]
    public class LineOfSightSettings
    {
        public ToggleNode RequireLineOfSight { get; set; } = new(true);
        public ToggleNode ConsiderTerrainHeight { get; set; } = new(true);
        public RangeNode<float> TerrainHeightWeight { get; set; } = new(1.0f, 0f, 5f);
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
public class CombatSettings
{
    public ToggleNode EnableCombatMode { get; set; } = new(true);
    public RangeNode<float> CombatRange { get; set; } = new(50f, 1f, 1000f);
    public ListNode AvailableStrategies { get; set; } = new ListNode();
    public ContentNode<ActiveSkill> Skills { get; set; } = new ContentNode<ActiveSkill>()
    {
        EnableItemCollapsing = true,
        EnableControls = false,
    };

    public ContentNode<ActiveSkill> MovementSkills { get; set; } = new ContentNode<ActiveSkill>()
    {
        EnableItemCollapsing = true,
        EnableControls = false,
    };

    public FollowSettings Follow { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public class FollowSettings
    {
        public ToggleNode Enable { get; set; } = new(true);
        public RangeNode<int> KeepWithinDistance { get; set; } = new(200, 10, 1000);
        public RangeNode<int> TransitionDistance { get; set; } = new(500, 100, 5000);

        [Menu("Zone Update Buffer (ms)")]
        public RangeNode<int> ZoneUpdateBuffer { get; set; } = new(1000, 500, 5000);

        public ToggleNode CloseFollow { get; set; } = new(true);
        public ToggleNode DashEnabled { get; set; } = new(false);

        [Menu("Input Frequency (ms)", "Minimum delay between successive movement inputs")]
        public RangeNode<int> InputFrequency { get; set; } = new(50, 1, 100);

        public RangeNode<int> LeaderNearbyDistance { get; set; } = new(500, 100, 2000);
        public RangeNode<int> LeaderJumpDistance { get; set; } = new(2000, 500, 5000);
        public ToggleNode EnablePathfindingFallback { get; set; } = new(true);
        public RangeNode<int> MaxPathNodesPerTick { get; set; } = new(6, 1, 20);

        public FollowVisualSettings Visual { get; set; } = new();
        public FollowDebugSettings Debug { get; set; } = new();

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
    }
}