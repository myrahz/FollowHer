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

namespace FollowHer.Routines.PaladinSupp.Strategy
{
    public class SkillPriority
    {
        private readonly GameController _gameController;
        private readonly Dictionary<string, int> _skillPriorities = new()
        {
            { "EnduringCry", 1 },      // Highest priority
            { "AncestralCry", 2 },
            { "BattlemagesCry", 3 },
            { "Smite", 4 },
            { "PyroclastMine", 5 },
            { "PyroclastMineAltX", 6 },
            { "SnipersMark", 7 },
            { "Stormbrand", 8 }        // Lowest priority
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

            var SnipersMark = usableSkills.FirstOrDefault(x => x.Name == "SnipersMark");
            var enduringCry = usableSkills.FirstOrDefault(x => x.Name == "EnduringCry");
            var PyroclastMine = usableSkills.FirstOrDefault(x => x.Name == "PyroclastMine");
            var PyroclastMineAlt = usableSkills.FirstOrDefault(x => x.Name == "PyroclastMineAltX");
            var AncestralCry = usableSkills.FirstOrDefault(x => x.Name == "AncestralCry");
            var BattlemagesCry = usableSkills.FirstOrDefault(x => x.Name == "BattlemagesCry");
            var Smite = usableSkills.FirstOrDefault(x => x.Name == "Smite");
            var Stormbrand = usableSkills.FirstOrDefault(x => x.Name == "Stormbrand");



            //if (player.GetComponent<Life>().HPPercentage <= 0.85 && enduringCry != null && enduringCry.Enabled) //Enduring Cry if Health < 85
            //{
            //    var validTargets = targetSelector.GetValidTargets(enduringCry);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (enduringCry, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (EnduringCryTimer() < 1f && enduringCry != null && enduringCry.Enabled)  //Enduring Cry if timer < 1
            //{
            //    var validTargets = targetSelector.GetValidTargets(enduringCry);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (enduringCry, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (BattlemagesCryTimer() < 1f && BattlemagesCry != null && BattlemagesCry.Enabled) //Battlemage's Cry if timer < 1
            //{
            //    var validTargets = targetSelector.GetValidTargets(BattlemagesCry);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (BattlemagesCry, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (AncestralCryTimer() < 1f && AncestralCry != null && AncestralCry.Enabled) //Ancestral Cry if timer < 1
            //{
            //    var validTargets = targetSelector.GetValidTargets(AncestralCry);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (AncestralCry, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (!StormBrandNearTarget() && Stormbrand != null && Stormbrand.Enabled)  //Storm brand if no stormbrand near target target
            //{
            //    var validTargets = targetSelector.GetValidTargets(Stormbrand);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (Stormbrand, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (AncestralCryTimer() < 1f && NearbyMonsters && Smite != null && Smite.Enabled)  //Smite if timer < 1
            //{
            //    var validTargets = targetSelector.GetValidTargets(Smite);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (Smite, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (AncestralCryTimer() < 1f && RareMonsters && Smite != null && Smite.Enabled)  //Smite if timer < 1
            //{
            //    var validTargets = targetSelector.GetValidTargets(Smite);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    var aux = validTargets.
            //    bestAction = (Smite, new EntityInfo(bestTarget, _gameController));

            //}





            //}
            //else if (AncestralCryTimer() < 1f && AncestralCry != null && AncestralCry.Enabled) //Ancestral Cry if timer < 1
            //{
            //    var validTargets = targetSelector.GetValidTargets(AncestralCry);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (AncestralCry, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (!StormBrandNearTarget() && Stormbrand != null && Stormbrand.Enabled)  //Storm brand if no stormbrand near target target
            //{
            //    var validTargets = targetSelector.GetValidTargets(Stormbrand);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (Stormbrand, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (AncestralCryTimer() < 1f && NearbyMonsters && Smite != null && Smite.Enabled)  //Smite if timer < 1
            //{
            //    var validTargets = targetSelector.GetValidTargets(Smite);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    bestAction = (Smite, new EntityInfo(bestTarget, _gameController));

            //}
            //else if (AncestralCryTimer() < 1f && RareMonsters && Smite != null && Smite.Enabled)  //Smite if timer < 1
            //{
            //    var validTargets = targetSelector.GetValidTargets(Smite);
            //    var bestTarget = validTargets.FirstOrDefault();
            //    var aux = validTargets.
            //    bestAction = (Smite, new EntityInfo(bestTarget, _gameController));







            //if rarity >= rare

            //    Snipers mark if no mark
            //    Pyroclast mine if no mines near




            foreach (var skill in usableSkills)
            {
                if (!skill.Enabled) continue;
                if (!skill.CanUse) continue;

                var validTargets = targetSelector.GetValidTargets(skill);
                if (!validTargets.Any()) continue;

                // Fix 1: Remove the negation (!) and fix the logic
                if (skill.Name == "EnduringCry")
                {
                    // Use Enduring Cry if health <= 85% OR timer < 1f
                    if (!(player.GetComponent<Life>().HPPercentage <= 0.85 || EnduringCryTimer() < 1f))
                        continue;
                }
                else if (skill.Name == "AncestralCry")
                {
                    // Use Ancestral Cry if timer < 1f
                    if (!(AncestralCryTimer() < 1f))
                        continue;
                }
                else if (skill.Name == "BattlemagesCry")
                {
                    // Use Battlemage's Cry if timer < 1f
                    if (!(BattlemagesCryTimer() < 1f))
                        continue;
                }
                else if (skill.Name == "Stormbrand")
                {
                    // Use Storm Brand if NO storm brand near target
                    if (StormBrandNearTarget())
                        continue;
                }
                else if (skill.Name == "Smite")
                {
                    // Use Smite if timer < 1f
                    if (!(SmiteTimer() < 1f))
                        continue;
                }
                // Add other skills as needed (SnipersMark, PyroclastMine)

                foreach (var target in validTargets)
                {
                    var weight = priorityCalculator.GetEntityWeight(target);
                    if (weight.HasValue && weight.Value > maxWeight)
                    {
                        maxWeight = weight.Value;
                        bestAction = (skill, new EntityInfo(target, _gameController));
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
    }
}