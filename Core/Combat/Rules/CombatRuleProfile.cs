using System.Collections.Generic;

namespace FollowHer.Core.Combat.Rules;

/// <summary>A named, ordered set of CombatRules - the rule-based replacement for what used to be
/// "which hardcoded Routine class is selected." List order is priority.</summary>
public class CombatRuleProfile
{
    public string Name { get; set; } = "";
    public List<CombatRule> Rules { get; set; } = new();

    /// <summary>Melee builds (Righteous Fire): while attacking is allowed and no rule fires this tick,
    /// walk up to the nearest valid monster until it is within this many world units, and stand there
    /// instead of returning to the leader. 0 = off.</summary>
    public int MeleeRange { get; set; }
}
