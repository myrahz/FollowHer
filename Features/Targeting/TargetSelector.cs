using System;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FollowHer.Features.Targeting.EntityInformation;
using FollowHer.Features.Targeting.Priority;
using FollowHer.Features.Targeting.Density;
using FollowHer.Utils;
using FollowHer.Settings;
using System.Collections.Generic;
using FollowHer.Core.Combat.Skills;

namespace FollowHer.Features.Targeting
{
    public class TargetSelector : IDisposable
    {
        private readonly GameController _gameController;
        private readonly EntityScanner _entityScanner;
        private readonly PriorityCalculator _priorityCalculator;
        private readonly DensityAnalyzer _densityAnalyzer;
        private readonly LineOfSight _lineOfSight;
        private readonly TargetingSettingsMonitor _settingsMonitor;

        private Entity _currentTarget;
        private DateTime _lastSelectionTime;
        private float _targetSwitchCooldown = 0.5f;
        private float _minWeightDifferenceForSwitch = 0.5f;
        private float _maxTargetDistance = 100f;
        private bool _prioritizeCurrentTarget = true;

        private float _baseClusterBonus = 0.1f;
        private float _maxClusterBonus = 2.0f;

        public TargetSelector(
            GameController gameController,
            EntityScanner entityScanner,
            PriorityCalculator priorityCalculator,
            LineOfSight lineOfSight)
        {
            _gameController = gameController;
            _entityScanner = entityScanner;
            _priorityCalculator = priorityCalculator;
            _densityAnalyzer = new DensityAnalyzer(gameController, entityScanner);
            _lineOfSight = lineOfSight;
            
            _settingsMonitor = new TargetingSettingsMonitor(FollowHer.Instance.Settings.Targeting);
            _settingsMonitor.OnSettingsChanged += () => Configure();
        }

        public void Configure()
        {
            _targetSwitchCooldown = FollowHer.Instance.Settings.Targeting.TargetSwitchThreshold;
            _minWeightDifferenceForSwitch = FollowHer.Instance.Settings.Targeting.MinWeightDifferenceForSwitch;
            _maxTargetDistance = FollowHer.Instance.Settings.Targeting.MaxTargetRange;
            _prioritizeCurrentTarget = FollowHer.Instance.Settings.Targeting.PrioritizeCurrentTarget;

            var densitySettings = FollowHer.Instance.Settings.Targeting.Density;
            _baseClusterBonus = densitySettings.BaseClusterBonus;
            _maxClusterBonus = densitySettings.MaxClusterBonus;

            _entityScanner.SetScanRange(FollowHer.Instance.Settings.Targeting.ScanRadius);
            _densityAnalyzer.Configure(
                densitySettings.ClusterRadius,
                densitySettings.MinClusterSize,
                FollowHer.Instance.Settings.Targeting.LineOfSight.RequireLineOfSight
            );

            var priorities = FollowHer.Instance.Settings.Targeting.Priorities;
            var rarity = priorities.Rarity;
            _priorityCalculator.Configure(
                distanceWeight: priorities.DistanceWeight,
                healthWeight: priorities.Health.HealthWeight,
                rarityWeight: rarity.ConsiderRarity ? 1.0f : 0f,
                maxTargetDistance: FollowHer.Instance.Settings.Targeting.MaxTargetRange,
                preferHigherHealth: priorities.Health.PreferHigherHealth,
                normalRarityMultiplier: rarity.NormalWeight,
                magicRarityMultiplier: rarity.MagicWeight,
                rareRarityMultiplier: rarity.RareWeight,
                uniqueRarityMultiplier: rarity.UniqueWeight
            );

            _currentTarget = null;
        }

        public void Update()
        {
            if (_gameController?.Player == null) return;

            try
            {
                var playerPos = _gameController.Player.GridPosNum;
                _entityScanner.Scan();

                var entities = _entityScanner.GetEntitiesInRange(_maxTargetDistance);
                _priorityCalculator.UpdatePriorities(entities);
                _densityAnalyzer.Update(entities, new List<LineOfSightDataType> { LineOfSightDataType.Terrain });

                UpdateTargetSelection(playerPos);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[TargetSelector] Error during update: {ex.Message}");
            }
        }

        public void Update(IReadOnlyCollection<ActiveSkill> skills)
        {
            if (_gameController?.Player == null) return;
            try
            {
                var playerPos = _gameController.Player.GridPosNum;
                _entityScanner.Scan(skills);
                var entities = _entityScanner.GetEntitiesInRange(_maxTargetDistance);
                _priorityCalculator.UpdatePriorities(entities);
                var losTypes = skills.Select(s => LineOfSight.Parse(s.LineOfSightType.Value)).Distinct().ToList();
                _densityAnalyzer.Update(entities, losTypes);
                UpdateTargetSelection(playerPos);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[TargetSelector] Error during update with skills: {ex.Message}");
            }
        }

        private void UpdateTargetSelection(Vector2 playerPos)
        {
            var currentTime = DateTime.UtcNow;
            var timeSinceLastSelection = (currentTime - _lastSelectionTime).TotalSeconds;

            if (_currentTarget != null)
            {
                if (!IsTargetValid(_currentTarget))
                {
                    HandleTargetLost();
                    _currentTarget = null;
                }
                else
                {
                    var distance = Vector2.Distance(playerPos, _currentTarget.GridPosNum);
                    if (distance > _maxTargetDistance)
                    {
                        _currentTarget = null;
                    }
                }
            }

            if (_currentTarget == null || timeSinceLastSelection >= _targetSwitchCooldown)
            {
                SelectNewTarget(playerPos);
            }
        }

        private void SelectNewTarget(Vector2 playerPos)
        {
            var entities = _entityScanner.GetEntitiesInRange(_maxTargetDistance).ToList();
            if (!entities.Any())
            {
                if (_currentTarget != null)
                {
                    HandleTargetLost();
                }
                return;
            }

            var bestTarget = FindBestTarget(entities, playerPos);
            if (bestTarget == null) return;

            var shouldSwitch = ShouldSwitchTarget(bestTarget);
            if (shouldSwitch)
            {
                _currentTarget = bestTarget;
                _lastSelectionTime = DateTime.UtcNow;
            }
        }

        public List<Entity> GetValidTargets(ActiveSkill skill)
        {
            var entities = _entityScanner.GetEntitiesInRange(_maxTargetDistance);

            // The RequireLineOfSight toggle used to be honored only by the density analyzer while
            // this filter enforced LOS unconditionally - so turning it off did nothing for actual
            // target selection. Now it's the real switch: off means distance/targetability only.
            var requireLos = FollowHer.Instance.Settings.Targeting.LineOfSight.RequireLineOfSight;
            var losType = LineOfSight.Parse(skill.LineOfSightType.Value);

            return entities.Where(e =>
            {
                if (!IsTargetValid(e)) return false;
                if (!requireLos) return true;

                var hasLos = _entityScanner.GetEntityLineOfSight(e, losType);
                return hasLos.HasValue && hasLos.Value;
            }).ToList();
        }

        /// <summary>Per-stage counts for the combat diagnostic - pins down whether "no valid
        /// target" is a range problem, a targetability problem, or line-of-sight filtering.</summary>
        public string DescribeTargetFiltering(ActiveSkill skill)
        {
            var entities = _entityScanner.GetEntitiesInRange(_maxTargetDistance).ToList();
            var losType = LineOfSight.Parse(skill.LineOfSightType.Value);
            var requireLos = FollowHer.Instance.Settings.Targeting.LineOfSight.RequireLineOfSight;

            int valid = 0, withLos = 0;
            foreach (var e in entities)
            {
                if (!IsTargetValid(e)) continue;
                valid++;
                var los = _entityScanner.GetEntityLineOfSight(e, losType);
                if (los.HasValue && los.Value) withLos++;
            }

            return $"inRange={entities.Count}, targetable={valid}, withLoS={withLos} " +
                   $"(losLayer={losType}, requireLoS={requireLos.Value}, maxRange={_maxTargetDistance})";
        }

        private Entity FindBestTarget(System.Collections.Generic.IEnumerable<Entity> entities, Vector2 playerPos)
        {
            Entity bestTarget = null;
            float bestWeight = float.MinValue;

            foreach (var entity in entities)
            {
                if (!IsTargetValid(entity)) continue;

                var weight = CalculateFinalWeight(entity);
                if (weight > bestWeight)
                {
                    bestWeight = weight;
                    bestTarget = entity;
                }
            }

            return bestTarget;
        }

        private float CalculateFinalWeight(Entity entity)
        {
            var baseWeight = _priorityCalculator.GetEntityWeight(entity);
            if (!baseWeight.HasValue) return float.MinValue;

            var finalWeight = baseWeight.Value;

            if (FollowHer.Instance.Settings.Targeting.Density.EnableClustering)
            {
                var density = _densityAnalyzer.GetDensityAtPosition(entity.GridPosNum);
                var densitySettings = FollowHer.Instance.Settings.Targeting.Density;

                if (density != null)
                {
                    var densityBonus = CalculateDensityBonus(density);
                    finalWeight *= (1 + densityBonus);

                    if (densitySettings.EnableCoreBonus)
                    {
                        var distanceFromCenter = Vector2.Distance(density.Center, entity.GridPosNum);
                        if (distanceFromCenter <= density.Radius * densitySettings.CoreRadiusPercent)
                        {
                            finalWeight *= densitySettings.CoreBonusMultiplier;
                        }
                    }
                }
                else if (densitySettings.EnableIsolationPenalty && !IsEntityInAnyClusters(entity))
                {
                    finalWeight *= densitySettings.IsolationPenaltyMultiplier;
                }
            }

            return finalWeight;
        }

        private bool IsEntityInAnyClusters(Entity entity)
        {
            foreach (var cluster in _densityAnalyzer.GetAllDensities())
            {
                if (cluster.ContainsEntity(entity))
                {
                    return true;
                }
            }
            return false;
        }

        private float CalculateDensityBonus(DensityInfo density)
        {
            var densitySettings = FollowHer.Instance.Settings.Targeting.Density;
            float bonus = _baseClusterBonus + (_maxClusterBonus - _baseClusterBonus) *
                ((float)density.Entities.Count / densitySettings.MinClusterSize);
            return Math.Min(bonus, _maxClusterBonus);
        }

        private bool ShouldSwitchTarget(Entity newTarget)
        {
            if (_currentTarget == null) return true;
            if (newTarget.Address == _currentTarget.Address) return false;

            var newWeight = CalculateFinalWeight(newTarget);
            var currentWeight = CalculateFinalWeight(_currentTarget);

            if (!_prioritizeCurrentTarget) return newWeight > currentWeight;

            return newWeight > currentWeight + _minWeightDifferenceForSwitch;
        }

        private void HandleTargetLost()
        {
            _currentTarget = null;
        }

        private bool IsTargetValid(Entity entity)
        {
            if (entity == null || !entity.IsValid || entity.IsDead || !entity.IsAlive || !entity.IsTargetable)
                return false;

            var distance = _entityScanner.GetEntityDistance(entity);
            return distance.HasValue && distance.Value <= _maxTargetDistance;
        }

        public void Clear()
        {
            _currentTarget = null;
            _entityScanner.Clear();
            _densityAnalyzer.Clear();
            _lineOfSight.Clear();
        }

        public void Dispose()
        {
            _settingsMonitor.Dispose();
            _entityScanner.Dispose();
            _lineOfSight.Dispose();
        }
    }
}