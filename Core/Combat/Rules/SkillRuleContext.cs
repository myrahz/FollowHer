using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace FollowHer.Core.Combat.Rules;

/// <summary>The facade exposed to rule condition expressions - everything a condition can read
/// about the candidate target and the player, built fresh per (rule, candidate) evaluation.</summary>
public class SkillRuleContext
{
    private readonly GameController _gameController;
    private readonly Entity _targetEntity;

    public SkillRuleContext(GameController gameController, Entity targetEntity)
    {
        _gameController = gameController;
        _targetEntity = targetEntity;
        Target = new EntityRuleInfo(targetEntity);
        Player = new EntityRuleInfo(gameController?.Player);
    }

    public EntityRuleInfo Target { get; }
    public EntityRuleInfo Player { get; }

    // Excludes the target itself so a check like "am I fighting a pack" doesn't count the target
    // it's already being asked about.
    public int NearbyMonsterCount(float radius)
    {
        if (_targetEntity == null) return 0;

        return _gameController?.EntityListWrapper?.ValidEntitiesByType?.GetValueOrDefault(EntityType.Monster)?
            .Count(m => m != null && m.IsAlive && m.Address != _targetEntity.Address &&
                        m.Distance(_targetEntity) <= radius) ?? 0;
    }

    // Pack counts: hostile, living monsters within radius (world units) of the target, the target
    // itself included - so "PackCount(60, MonsterRarity.Rare) >= 1" is true when the target is a rare.
    private IEnumerable<Entity> PackMembers(float radius)
    {
        if (_targetEntity == null) return Enumerable.Empty<Entity>();

        return _gameController?.EntityListWrapper?.ValidEntitiesByType?.GetValueOrDefault(EntityType.Monster)?
                   .Where(m => m != null && m.IsValid && m.IsAlive && m.IsHostile && m.Distance(_targetEntity) <= radius)
               ?? Enumerable.Empty<Entity>();
    }

    public int PackCount(float radius, MonsterRarity rarity) => PackMembers(radius).Count(m => m.Rarity == rarity);

    public int PackCountAny(float radius) => PackMembers(radius).Count();

    /// <summary>Monsters of every rarity except the given one - e.g. "anything that isn't a unique".</summary>
    public int PackCountNot(float radius, MonsterRarity rarity) => PackMembers(radius).Count(m => m.Rarity != rarity);

    /// <summary>The "worth a cooldown" test shared by the totem/trap rules: 2+ magic, 1+ rare,
    /// 1+ unique, or 7+ monsters of any rarity around the target.</summary>
    public bool PackWorthy(float radius)
    {
        var pack = PackMembers(radius).ToList();
        return pack.Count >= 7 ||
               pack.Count(m => m.Rarity == MonsterRarity.Magic) >= 2 ||
               pack.Any(m => m.Rarity == MonsterRarity.Rare || m.Rarity == MonsterRarity.Unique);
    }

    // Generalizes the old RF routine's hardcoded "is there already a totem near this target"
    // check (it matched a literal metadata path substring within a fixed radius).
    public bool HasNearbyEntityWithMetadata(string pathSubstring, float radius)
    {
        if (_targetEntity == null || string.IsNullOrEmpty(pathSubstring) || _gameController?.Entities == null)
        {
            return false;
        }

        return _gameController.Entities.Any(e =>
            e != null && e.Path != null &&
            e.Path.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase) &&
            e.Distance(_targetEntity) <= radius);
    }

    // Ruthless support counter for a skill, read from the RuthlessTracker plugin over the plugin
    // bridge: 0..max-1, where max-1 means the next swing is the big hit. -1 when RuthlessTracker
    // isn't loaded, the skill has no Ruthless, or the tracker isn't synced yet - so a check like
    // "RuthlessStage(...) == 2" is simply false rather than wrong in those cases.
    private static Func<string, int> _ruthlessStage;

    public int RuthlessStage(string skillName)
    {
        try
        {
            _ruthlessStage ??= _gameController?.PluginBridge?.GetMethod<Func<string, int>>("RuthlessTracker.GetStage");
            return _ruthlessStage?.Invoke(skillName) ?? -1;
        }
        catch
        {
            _ruthlessStage = null;
            return -1;
        }
    }

    // True while the named skill is on cooldown (e.g. a cry that was just used). An unknown skill
    // counts as not on cooldown.
    public bool SkillOnCooldown(string skillName)
    {
        var skill = _gameController?.Player?.GetComponent<Actor>()?.ActorSkills?
            .FirstOrDefault(s => s.Name == skillName);
        return skill?.IsOnCooldown ?? false;
    }

    // HealthPercent/EnergyShieldPercent are 0-1 fractions, matching the underlying Life component
    // and every existing hardcoded routine's own convention (e.g. "HPPercentage <= 0.85").
    public class EntityRuleInfo
    {
        private readonly Entity _entity;

        public EntityRuleInfo(Entity entity)
        {
            _entity = entity;
        }

        public float HealthPercent => _entity?.GetComponent<Life>()?.HPPercentage ?? 0f;
        public float EnergyShieldPercent => _entity?.GetComponent<Life>()?.ESPercentage ?? 0f;
        public float DistanceToPlayer => _entity?.DistancePlayer ?? float.MaxValue;
        public MonsterRarity Rarity => _entity?.Rarity ?? MonsterRarity.White;
        public bool IsAlive => _entity?.IsAlive ?? false;

        public bool HasBuff(string name)
        {
            if (_entity == null || !_entity.TryGetComponent<Buffs>(out var buffs)) return false;
            return buffs.BuffsList?.Any(b => b.Name == name) ?? false;
        }

        public float BuffTimer(string name)
        {
            if (_entity == null || !_entity.TryGetComponent<Buffs>(out var buffs)) return 0f;
            return buffs.BuffsList?.FirstOrDefault(b => b.Name == name)?.Timer ?? 0f;
        }

        // Warcry-style skills track their remaining empowered/exerted attack count in a buff
        // named "display_num_empowered_attacks" whose SourceSkill identifies which cry it came
        // from - ported from the old Sunder routine's GetRemainingExertedAttacks helper.
        // True if an empowered attack was spent in the last N seconds (see EmpoweredAttackTracker).
        public bool EmpoweredAttackWithin(float seconds) => EmpoweredAttackTracker.SpentWithin(seconds);

        public int ExertedAttacksRemaining(string sourceSkillName)
        {
            if (_entity == null || !_entity.TryGetComponent<Buffs>(out var buffs)) return 0;
            var buff = buffs.BuffsList?.FirstOrDefault(b =>
                b.SourceSkill?.Name == sourceSkillName && b.Name == "display_num_empowered_attacks");
            return buff?.BuffCharges ?? 0;
        }
    }
}
