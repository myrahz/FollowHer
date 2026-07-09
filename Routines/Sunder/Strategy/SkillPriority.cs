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

namespace FollowHer.Routines.Sunder.Strategy
{
    public class SkillPriority
    {
        private readonly GameController _gameController;
        private readonly Dictionary<string, int> _skillPriorities = new()
        {
            { "Vulnerability", 1 },      // Highest priority
            { "IntimidatingCry", 2 },      // Highest priority
            { "SeismicCry", 3 },      // Highest priority
            { "InfernalCry", 4 },      // Highest priority
            { "GeneralsCry", 5 },      // Highest priority
            { "RallyingCry", 6 },      // Highest priority
            { "AncestralCry", 7 },
            { "BattlemagesCry", 8 },
            { "Sunder", 9},        // Lowest priority
            { "GroundSlam", 10},        // Lowest priority
            { "Earthshatter", 11},        // Lowest priority
        };

        private const float MELEE_RANGE = 20.0f;
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

            var sunder = usableSkills.FirstOrDefault(x => x.Name == "Sunder");
            var vulnerability = usableSkills.FirstOrDefault(x => x.Name == "Vulnearbility");
            var intimidatingCry = usableSkills.FirstOrDefault(x => x.Name == "IntimidatingCry");
            var seismicCry = usableSkills.FirstOrDefault(x => x.Name == "SeismicCry");
            var infernalCry = usableSkills.FirstOrDefault(x => x.Name == "InfernalCry");
            var generalsCry = usableSkills.FirstOrDefault(x => x.Name == "GeneralsCry");
            var rallyingCry = usableSkills.FirstOrDefault(x => x.Name == "RallyingCry");
            var battlemagesCry = usableSkills.FirstOrDefault(x => x.Name == "BattlemagesCry");







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
                    // 2) WARCRY PRIORITY BASED ON REMAINING EXERTED ATTACKS
                    // ============================================================

                    // Use your function: GetRemainingExertedAttacks(skillName)
                    bool ShouldUseCry(string cryName)
                    {
                        var remaining = GetRemainingExertedAttacks(cryName);
                        return remaining == 0;
                    }

                    // ---- PRIORITY ORDER ----
                    if (skill.Name == "IntimidatingCry" && ShouldUseCry("IntimidatingCry"))
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                        continue;
                    }

                    if (skill.Name == "SeismicCry" && ShouldUseCry("SeismicCry"))
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                        continue;
                    }

                    if (skill.Name == "InfernalCry" && ShouldUseCry("InfernalCry"))
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                        continue;
                    }

                    if (skill.Name == "GeneralsCry" && ShouldUseCry("GeneralsCry"))
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                        continue;
                    }

                    if (skill.Name == "RallyingCry" && ShouldUseCry("RallyingCry"))
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                        continue;
                    }

                    if (skill.Name == "BattlemagesCry" && ShouldUseCry("BattlemagesCry"))
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                        continue;
                    }


                    // ============================================================
                    // 3) FINALLY — SUnder
                    // ============================================================
                    if (skill.Name == "Sunder")
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                    } 
                    
                    if (skill.Name == "GroundSlam")
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                    }                    
                    if (skill.Name == "Earthshatter")
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

                return buffs.BuffsList?.Any(buff => buff.Name == "vulnerability") ?? false;
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
        
        private float SmiteTimer()
        {

            // support_static_charge
            var player = _gameController.Player;
            try
            {
                if (!player.TryGetComponent<Buffs>(out var buffs))
                    return 0;
                var SmiteBuff = buffs.BuffsList?.FirstOrDefault(buff => buff.Name == "smite_buff");

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