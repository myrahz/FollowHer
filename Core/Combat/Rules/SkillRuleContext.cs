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
        public int ExertedAttacksRemaining(string sourceSkillName)
        {
            if (_entity == null || !_entity.TryGetComponent<Buffs>(out var buffs)) return 0;
            var buff = buffs.BuffsList?.FirstOrDefault(b =>
                b.SourceSkill?.Name == sourceSkillName && b.Name == "display_num_empowered_attacks");
            return buff?.BuffCharges ?? 0;
        }
    }
}
