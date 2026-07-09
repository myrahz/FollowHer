using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using FollowHer.Core.Combat.Skills;
using FollowHer.Features.Targeting.EntityInformation;
using System;
using System.Collections.Generic;
using System.Linq;
using FollowHer.Features.Targeting;
using FollowHer.Features.Targeting.Priority;

namespace FollowHer.Routines.Retaliation.Strategy
{
    public class SkillPriority
    {
        private readonly GameController _gameController;
        private readonly Dictionary<string, int> _skillPriorities = new()
        {
            { "Vulnerability", 1 },      // Highest priority
            { "Eviscerate", 2 },      // Highest priority
            { "CrushingFist", 3 },      // Highest priority
            { "ShieldCrush", 4 },      // Highest priority
            { "Bladestorm", 5 },      // Highest priority

        };

        private const float MELEE_RANGE = 10.0f;
        private const float NEARBY_MONSTER_RADIUS = 30.0f;
        public SkillPriority(GameController gameController)
        {
            _gameController = gameController;
        }

        public (ActiveSkill skill, EntityInfo target) GetBestAction(
            IReadOnlyCollection<ActiveSkill> availableSkills,
            TargetSelector targetSelector,
            PriorityCalculator priorityCalculator,
            SkillMonitor skillMonitor)
        {
            (ActiveSkill skill, EntityInfo target) bestAction = (null, null);
            float maxWeight = float.MinValue;

            var usableSkills = availableSkills
                .Where(s => skillMonitor.CanUseSkill(s) && _skillPriorities.ContainsKey(s.Name))
                .OrderBy(s => _skillPriorities[s.Name]);


            var player = _gameController.Player;

            var eviscerate = usableSkills.FirstOrDefault(x => x.Name == "Eviscerate");
            var vulnerability = usableSkills.FirstOrDefault(x => x.Name == "Vulnerability");
            var crushingFist = usableSkills.FirstOrDefault(x => x.Name == "CrushingFist");
            var shieldCrush = usableSkills.FirstOrDefault(x => x.Name == "ShieldCrush");
            var bladestorm = usableSkills.FirstOrDefault(x => x.Name == "Bladestorm");








            foreach (var skill in usableSkills)
            {
                if (!skill.Enabled || !skill.CanUse)
                    continue;

                var validTargets = targetSelector.GetValidTargets(skill);
                if (!validTargets.Any())
                    continue;

                foreach (var target in validTargets)
                {
                    // ============================================================
                    // 1) Vulnerability on Rare/Unique if not applied
                    // ============================================================
                    if (skill.Name == "Vulnerability")
                    {
                        if ((target.Rarity == MonsterRarity.Rare || target.Rarity == MonsterRarity.Unique)
                            && !HasVulnerability(target))
                        {
                            var weight = priorityCalculator.GetEntityWeight(target);
                            if (weight.HasValue && weight.Value > maxWeight)
                            {
                                maxWeight = weight.Value;
                                bestAction = (skill, new EntityInfo(target, _gameController));
                            }
                        }
                        continue;
                    }

                                        


                    // ============================================================
                    // 3) attacks
                    // ============================================================
                    if (skill.Name == "Eviscerate" && EviscerateTimer() > 0)
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                    } 
                    
                    if (skill.Name == "CrushingFist" && CrushingFistTimer() > 0)
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                    }                    
                    if (skill.Name == "ShieldCrush")
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                    }
                    if (skill.Name == "Bladestorm")
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                    }
                }
            }

            return bestAction;

        }

        private bool StormBrandNearTarget()
        {
            return true;
        }
        private bool HasSnipersMark(Entity target)
        {

            try
            {
                if (!target.TryGetComponent<Buffs>(out var buffs))
                    return false;

                return buffs.BuffsList?.Any(buff => buff.Name == "snipers_mark") ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool HasVulnerability(Entity target)
        {

            try
            {
                if (!target.TryGetComponent<Buffs>(out var buffs))
                    return false;

                return buffs.BuffsList?.Any(buff => buff.Name == "curse_vulnerability") ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        private bool AnyNearbyMonsterHasSnipersMark(Entity target)
        {
            try
            {
                return _gameController.Entities
                    .Where(x => x?.Type.Equals(EntityType.Monster) ?? false)
                    .Where(x => x?.IsAlive ?? false)
                    .Where(x => x.Distance(target) <= NEARBY_MONSTER_RADIUS)
                    .Any(monster =>
                    {
                        if (!monster.TryGetComponent<Buffs>(out var buffs))
                            return false;
                        return buffs.BuffsList?.Any(buff => buff.Name == "snipers_mark") ?? false;
                    });
            }
            catch (Exception)
            {
                return false;
            }
        }


        private float AncestralCryTimer()
        {

            // support_static_charge
            var player = _gameController.Player;
            try
            {
                if (!player.TryGetComponent<Buffs>(out var buffs))
                    return 0;
                var AncestralCryBuff = buffs.BuffsList?.FirstOrDefault(buff => buff.Name == "ancestral_cry");

                return AncestralCryBuff?.Timer ?? 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        } 
        
        private float EviscerateTimer()
        {

            // support_static_charge
            var player = _gameController.Player;
            try
            {
                if (!player.TryGetComponent<Buffs>(out var buffs))
                    return 0;
                var SmiteBuff = buffs.BuffsList?.FirstOrDefault(buff => buff.Name == "retaliation_evisceration_enabled");

                return SmiteBuff?.Timer ?? 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }                 
        
        private float CrushingFistTimer()
        {

            // support_static_charge
            var player = _gameController.Player;
            try
            {
                if (!player.TryGetComponent<Buffs>(out var buffs))
                    return 0;
                var SmiteBuff = buffs.BuffsList?.FirstOrDefault(buff => buff.Name == "retaliation_fist_enabled");

                return SmiteBuff?.Timer ?? 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }         
        private float EnduringCryTimer()
        {

            // support_static_charge
            var player = _gameController.Player;
            try
            {
                if (!player.TryGetComponent<Buffs>(out var buffs))
                    return 0;
                var EnduringCryBuff = buffs.BuffsList?.FirstOrDefault(buff => buff.Name == "enduring_cry");

                return EnduringCryBuff?.Timer ?? 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        } 
        private float BattlemagesCryTimer()
        {

            // support_static_charge
            var player = _gameController.Player;
            try
            {
                if (!player.TryGetComponent<Buffs>(out var buffs))
                    return 0;
                var BattlemagesCryBuff = buffs.BuffsList?.FirstOrDefault(buff => buff.Name == "divine_cry");

                return BattlemagesCryBuff?.Timer ?? 0f;
            }
            catch (Exception)
            {
                return 0;
            }
        }
        private int GetRemainingExertedAttacks(string crySkillName)
        {
            try
            {
                var player = _gameController.Player;

                if (!player.TryGetComponent<Buffs>(out var buffs))
                    return 0;

                // This buff ALWAYS contains empowered/exerted attack count
                var buff = buffs.BuffsList?.FirstOrDefault(b =>
                    b.SourceSkill?.Name == crySkillName &&
                    b.Name == "display_num_empowered_attacks");

                return buff?.Charges ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}