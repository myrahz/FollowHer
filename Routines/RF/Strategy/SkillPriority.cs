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

namespace FollowHer.Routines.RF.Strategy
{
    public class SkillPriority
    {
        private readonly GameController _gameController;
        private readonly Dictionary<string, int> _skillPriorities = new()
        {
            { "RighteousFire", 1 },      // Highest priority
            { "Punishment", 2 },      // Highest priority
            { "FireTrap", 3 },      // Highest priority
            { "Firestorm", 4 },      // Highest priority
            { "HolyFlameTotem", 5 },      // Highest priority
            { "DecoyTotem", 6 }      // Highest priority
           
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

            var fireTrap = usableSkills.FirstOrDefault(x => x.Name == "FireTrap");
            var firestorm = usableSkills.FirstOrDefault(x => x.Name == "Firestorm");
            var righteousFire = usableSkills.FirstOrDefault(x => x.Name == "RighteousFire");
            var punishment = usableSkills.FirstOrDefault(x => x.Name == "Punishment");
            var holyFlameTotem = usableSkills.FirstOrDefault(x => x.Name == "HolyFlameTotem");
            var decoyTotem = usableSkills.FirstOrDefault(x => x.Name == "DecoyTotem");








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
                    if (skill.Name == "Punishment")
                    {
                        if ((target.Rarity == MonsterRarity.Rare || target.Rarity == MonsterRarity.Unique)
                            && !HasPunishment(target))
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
                    // 2) RF Buff
                    // ============================================================


                    // ---- PRIORITY ORDER ----
                    if (skill.Name == "RighteousFire" && !HasRF() )
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                        continue;
                    }

                    if (skill.Name == "HolyFlameTotem")
                    {
                        if ((target.Rarity == MonsterRarity.Rare || target.Rarity == MonsterRarity.Unique)
                            && !HasNearbyHolyFlameTotem(target))
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
                    if (skill.Name == "DecoyTotem")
                    {
                        if ((target.Rarity == MonsterRarity.Rare || target.Rarity == MonsterRarity.Unique)
                            && !HasNearbyDecoyTotem(target))
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
                    // 3) FINALLY — damage Skills
                    // ============================================================
                    if (skill.Name == "Firetrap")
                    {
                        var weight = priorityCalculator.GetEntityWeight(target);
                        if (weight.HasValue && weight.Value > maxWeight)
                        {
                            maxWeight = weight.Value;
                            bestAction = (skill, new EntityInfo(target, _gameController));
                        }
                    } 
                    
                    if (skill.Name == "Firestorm")
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




        private bool HasPunishment(Entity target)
        {

            try
            {
                if (!target.TryGetComponent<Buffs>(out var buffs))
                    return false;

                return buffs.BuffsList?.Any(buff => buff.Name == "punishment") ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }




        private bool HasNearbyDecoyTotem(Entity target) // Metadata/Monsters/Totems/TauntTotem
        {
            try
            {
                return _gameController.Entities
                    .Where(x => x?.Type == EntityType.Monster && x.IsAlive)
                    .Where(x => x.Distance(target) <= NEARBY_MONSTER_RADIUS)
                    .Any(monster => monster.Path?.Contains("Metadata/Monsters/Totems/TauntTotem") ?? false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool HasNearbyHolyFlameTotem(Entity target) // Metadata/Monsters/Totems/HolyFireSprayTotem
        {
            try
            {
                return _gameController.Entities
                    .Where(x => x?.Type == EntityType.Monster && x.IsAlive)
                    .Where(x => x.Distance(target) <= NEARBY_MONSTER_RADIUS)
                    .Any(monster => monster.Path?.Contains("Metadata/Monsters/Totems/HolyFireSprayTotem") ?? false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool HasRF()
        {
            try
            {
                var player = _gameController.Player;

                if (!player.TryGetComponent<Buffs>(out var buffs))
                    return false;

                // This buff ALWAYS contains empowered/exerted attack count
                var buff = buffs.BuffsList?.FirstOrDefault(b =>
                   
                    b.Name == "righteous_fire");

                if (buff is null)
                {
                    return false; 
                }
                else
                {
                    return true;
                }

                
            }
            catch
            {
                return false;
            }
        }
    }
}