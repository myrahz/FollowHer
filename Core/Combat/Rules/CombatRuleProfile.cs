using System.Collections.Generic;

namespace FollowHer.Core.Combat.Rules;

/// <summary>A named, ordered set of CombatRules - the rule-based replacement for what used to be
/// "which hardcoded Routine class is selected." List order is priority.</summary>
public class CombatRuleProfile
{
    public string Name { get; set; } = "";
    public List<CombatRule> Rules { get; set; } = new();
}
