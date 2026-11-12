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
            BuildSlams(),
            BuildStaticStrikeHelix(),
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

    public const string StaticStrikeHelixName = "StaticStrikeHelix";

    // Static Strike + Intimidating Cry + Spectral Helix, driven by the RuthlessTracker plugin's
    // counter (0,1 build-up; 2 = the next Static Strike is the big hit). Priority order:
    //  1. counter at 2 and the cry is ready -> cry, so the big hit is empowered
    //  2. empowered attacks left from the cry -> Static Strike spends them
    //  3. Static Strike buff up while the cry recovers -> Spectral Helix
    //  4. otherwise build the counter up with Static Strike, but not right after an empowered
    //     attack and not while its buff is still running
    // "static_strike_buff" is the buff id from the game's BuffDefinitions; if the buff doesn't
    // seem to register in game, check the real name in the HUD's buff list and edit rules 3 and 4.
    public static CombatRuleProfile BuildStaticStrikeHelix() => new()
    {
        Name = StaticStrikeHelixName,
        Rules = new List<CombatRule>
        {
            Rule("IntimidatingCry", "RuthlessStage(\"StaticStrike\") == 2 && Player.ExertedAttacksRemaining(\"IntimidatingCry\") == 0"),
            Rule("StaticStrike", "Player.ExertedAttacksRemaining(\"IntimidatingCry\") >= 1"),
            Rule("SpectralHelix", "Player.HasBuff(\"static_strike_buff\") && SkillOnCooldown(\"IntimidatingCry\")"),
            Rule("StaticStrike", "RuthlessStage(\"StaticStrike\") >= 0 && RuthlessStage(\"StaticStrike\") < 2 && !Player.HasBuff(\"static_strike_buff\") && !Player.EmpoweredAttackWithin(2)"),
        },
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
            Rule("DecoyTotem", "(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !HasNearbyEntityWithMetadata(\"Metadata/Monsters/Totems/TauntTotem\", 30)"),
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

    public const string RFName = "RF";
    public const string SlamsName = "Slams";

    // Radius (world units) around the target that "pack" counts look at.
    private const string PackRadius = "60";

    // Slams: keep a Decoy Totem up on anything worth it, refresh the cry when its empowered hits run
    // out, then slam. Blood Rage sits below Sunder as asked, so it only gets a turn while Sunder is
    // on cooldown. Skill names must match what the skill bar reports (turn on "Log Why Not Attacking"
    // to see the discovered names); "BloodRage" is a guess.
    public static CombatRuleProfile BuildSlams() => new()
    {
        Name = SlamsName,
        Rules = new List<CombatRule>
        {
            Rule("DecoyTotem", $"PackWorthy({PackRadius}) && !HasNearbyEntityWithMetadata(\"Metadata/Monsters/Totems/TauntTotem\", 30)"),
            Rule("IntimidatingCry", "Player.ExertedAttacksRemaining(\"IntimidatingCry\") == 0"),
            Rule("Sunder"),
            Rule("BloodRage", $"!Player.HasBuff(\"blood_rage\") && PackCountNot({PackRadius}, MonsterRarity.Unique) >= 1"),
            Rule("GroundSlam"),
        },
    };

    // RF: Righteous Fire itself is not cast here (it is a toggle you keep up); this profile curses,
    // drops totems/traps/rays on worthwhile packs, and MeleeRange keeps the character in the monsters'
    // faces so the fire hits them. "ScorchingRayAltX" is a guess at the internal name of Scorching Ray
    // of Immolation, and the Elemental Weakness buff id is "curse_elemental_weakness".
    public static CombatRuleProfile BuildRF() => new()
    {
        Name = RFName,
        MeleeRange = 25,
        Rules = new List<CombatRule>
        {
            Rule("ElementalWeakness", "(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !Target.HasBuff(\"curse_elemental_weakness\")"),
            Rule("HolyFlameTotem", $"PackWorthy({PackRadius}) && !HasNearbyEntityWithMetadata(\"Metadata/Monsters/Totems/HolyFireSprayTotem\", 30)"),
            Rule("ScorchingRayAltX", $"PackWorthy({PackRadius})"),
            Rule("FireTrap", $"PackWorthy({PackRadius})"),
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
