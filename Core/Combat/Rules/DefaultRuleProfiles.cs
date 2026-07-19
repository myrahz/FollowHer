using System.Collections.Generic;

namespace FollowHer.Core.Combat.Rules;

/// <summary>Seeds one CombatRuleProfile per real (non-stub) old hardcoded Routine, so switching
/// to the rule-based engine doesn't leave you with nothing to select. Ported as faithfully as
/// possible from each Routine's Strategy/SkillPriority.cs; a few spots were bugs in the original
/// (dead branches, a case-mismatched skill name) rather than intended behavior, and are called
/// out inline where fixed instead of silently carried forward. Only used to seed
/// Settings.Combat.Profiles once, when it's empty - never overwrites edits you make afterward.</summary>
public static class DefaultRuleProfiles
{
    public static Dictionary<string, CombatRuleProfile> CreateDefaults()
    {
        var profiles = new List<CombatRuleProfile>
        {
            BuildAbsolution(),
            BuildConcLeveling(),
            BuildEConc(),
            BuildLightningStrike(),
            BuildPalaTest(),
            BuildPaladinSupp(),
            BuildRetaliation(),
            BuildRF(),
            BuildSunder(),
        };

        var result = new Dictionary<string, CombatRuleProfile>();
        foreach (var profile in profiles)
        {
            result[profile.Name] = profile;
        }

        return result;
    }

    private static CombatRule Rule(string skillName, string condition = "true") => new()
    {
        SkillName = skillName,
        Condition = condition,
    };

    private static CombatRuleProfile BuildAbsolution() => new()
    {
        Name = "Absolution",
        Rules = new List<CombatRule>
        {
            Rule("Absolution"),
        },
    };

    // Original treated all 4 skills as equally weighted (best-weight-wins across whichever were
    // off cooldown) rather than a real priority order - the new engine is strictly priority-first,
    // so this becomes "prefer ExplosiveConcoction, then PoisonousConcoction/AltX, then
    // SpectralThrow as filler once the earlier ones are on cooldown" instead.
    private static CombatRuleProfile BuildConcLeveling() => new()
    {
        Name = "ConcLeveling",
        Rules = new List<CombatRule>
        {
            Rule("ExplosiveConcoction"),
            Rule("PoisonousConcoction"),
            Rule("PoisonousConcoctionAltX"),
            Rule("SpectralThrow"),
        },
    };

    private static CombatRuleProfile BuildEConc() => new()
    {
        Name = "EConc",
        Rules = new List<CombatRule>
        {
            Rule("ExplosiveConcoction"),
        },
    };

    private static CombatRuleProfile BuildLightningStrike() => new()
    {
        Name = "LightningStrike",
        Rules = new List<CombatRule>
        {
            Rule("LightningStrike"),
        },
    };

    // The original PalaTest never applied any of PaladinSupp's buff-timer gating - every skill
    // just fired whenever it was the best-weighted option and off cooldown.
    private static CombatRuleProfile BuildPalaTest() => new()
    {
        Name = "PalaTest",
        Rules = new List<CombatRule>
        {
            Rule("PyroclastMine"),
            Rule("EnduringCry"),
            Rule("AncestralCry"),
            Rule("BattlemagesCry"),
            Rule("Smite"),
            Rule("SnipersMark"),
            Rule("Stormbrand"),
        },
    };

    // Stormbrand omitted: its original gate ("!StormBrandNearTarget()") could never pass because
    // StormBrandNearTarget() was hardcoded to always return true - it never actually fired.
    private static CombatRuleProfile BuildPaladinSupp() => new()
    {
        Name = "PaladinSupp",
        Rules = new List<CombatRule>
        {
            Rule("EnduringCry", "Player.HealthPercent <= 0.85 || Player.BuffTimer(\"enduring_cry\") < 1"),
            Rule("AncestralCry", "Player.BuffTimer(\"ancestral_cry\") < 1"),
            // Original read a "divine_cry" buff for a skill named BattlemagesCry - preserved as-is
            // (possibly a real mismatch, possibly the actual buff name; edit freely if wrong).
            Rule("BattlemagesCry", "Player.BuffTimer(\"divine_cry\") < 1"),
            Rule("Smite", "Player.BuffTimer(\"smite_buff\") < 1"),
            Rule("PyroclastMine"),
            Rule("PyroclastMineAltX"),
            Rule("SnipersMark"),
        },
    };

    private static CombatRuleProfile BuildRetaliation() => new()
    {
        Name = "Retaliation",
        Rules = new List<CombatRule>
        {
            Rule("Vulnerability", "(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !Target.HasBuff(\"curse_vulnerability\")"),
            Rule("Eviscerate", "Player.BuffTimer(\"retaliation_evisceration_enabled\") > 0"),
            Rule("CrushingFist", "Player.BuffTimer(\"retaliation_fist_enabled\") > 0"),
            Rule("ShieldCrush"),
            Rule("Bladestorm"),
        },
    };

    // "FireTrap" (correct casing, matching the real skill name) - the original checked
    // "Firetrap" (lowercase t) in its live branch, a typo that made the rule permanently dead
    // despite FireTrap being configured and off-cooldown-checked correctly elsewhere.
    private static CombatRuleProfile BuildRF() => new()
    {
        Name = "RF",
        Rules = new List<CombatRule>
        {
            Rule("Punishment", "(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !Target.HasBuff(\"punishment\")"),
            Rule("RighteousFire", "!Player.HasBuff(\"righteous_fire\")"),
            Rule("HolyFlameTotem", "(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !HasNearbyEntityWithMetadata(\"Metadata/Monsters/Totems/HolyFireSprayTotem\", 30)"),
            Rule("DecoyTotem", "(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !HasNearbyEntityWithMetadata(\"Metadata/Monsters/Totems/TauntTotem\", 30)"),
            Rule("FireTrap"),
            Rule("Firestorm"),
        },
    };

    // AncestralCry omitted: it was configured/cooldown-tracked but had no matching branch in the
    // original tick loop at all, so it never contributed an action.
    // Vulnerability here checks a "vulnerability" buff (Sunder's own original string) rather than
    // "curse_vulnerability" like Retaliation/RF use - kept as each routine originally had it.
    private static CombatRuleProfile BuildSunder() => new()
    {
        Name = "Sunder",
        Rules = new List<CombatRule>
        {
            Rule("Vulnerability", "(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !Target.HasBuff(\"vulnerability\")"),
            Rule("IntimidatingCry", "Player.ExertedAttacksRemaining(\"IntimidatingCry\") == 0"),
            Rule("SeismicCry", "Player.ExertedAttacksRemaining(\"SeismicCry\") == 0"),
            Rule("InfernalCry", "Player.ExertedAttacksRemaining(\"InfernalCry\") == 0"),
            Rule("GeneralsCry", "Player.ExertedAttacksRemaining(\"GeneralsCry\") == 0"),
            Rule("RallyingCry", "Player.ExertedAttacksRemaining(\"RallyingCry\") == 0"),
            Rule("BattlemagesCry", "Player.ExertedAttacksRemaining(\"BattlemagesCry\") == 0"),
            Rule("Sunder"),
            Rule("GroundSlam"),
            Rule("Earthshatter"),
        },
    };
}
