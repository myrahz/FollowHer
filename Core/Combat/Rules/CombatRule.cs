using System;
using Newtonsoft.Json;

namespace FollowHer.Core.Combat.Rules;

/// <summary>One entry in a CombatRuleProfile: use SkillName when Condition evaluates true for a
/// candidate target. List order (in the owning profile) is priority - first matching rule wins.</summary>
public class CombatRule
{
    public string SkillName { get; set; } = "";
    public string Condition { get; set; } = "true";
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    private Lazy<(Func<SkillRuleContext, bool> Func, string Error)> _compiled;

    public CombatRule()
    {
        ResetCompiledCondition();
    }

    // Called whenever the editor UI changes Condition, so the next Evaluate() picks up the edit
    // instead of reusing a stale compiled delegate.
    public void ResetCompiledCondition()
    {
        _compiled = new Lazy<(Func<SkillRuleContext, bool>, string)>(() => RuleExpressionCompiler.Compile(Condition));
    }

    [JsonIgnore]
    public string LastError => _compiled.Value.Error;

    public bool Evaluate(SkillRuleContext context)
    {
        var (func, _) = _compiled.Value;
        if (func == null) return false;

        try
        {
            return func(context);
        }
        catch
        {
            return false;
        }
    }
}
