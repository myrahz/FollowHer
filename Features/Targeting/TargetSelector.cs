using System;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
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
            _densityAnalyzer = new DensityAnalyzer(gameController, lineOfSight);
            _lineOfSight = lineOfSight;
            
            _settingsMonitor = new TargetingSettingsMonitor(FollowHer.Instance.Settings.Targeting);
            _settingsMonitor.OnSettingsChanged += () => Configure();
        }

        public void Configure()
        {
            _targetSwitchCooldown = FollowHer.Instance.Settings.Targeting.TargetSwitchThreshold;
            _minWeightDifferenceForSwitch = FollowHer.Instance.Settings.Targeting.TargetSwitchThreshold;
            _maxTargetDistance = FollowHer.Instance.Settings.Targeting.MaxTargetRange;

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
            _priorityCalculator.Configure(
                distanceWeight: priorities.DistanceWeight,
                healthWeight: priorities.Health.HealthWeight,
                rarityWeight: priorities.Rarity.ConsiderRarity ? 1.0f : 0f,
                maxTargetDistance: FollowHer.Instance.Settings.Targeting.MaxTargetRange,
                preferHigherHealth: priorities.Health.PreferHigherHealth
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
                        EventBus.Instance.Publish(new TargetOutOfRangeEvent(
                            _currentTarget,
                            distance,
                            _maxTargetDistance));
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
                var oldTarget = _currentTarget;
                _currentTarget = bestTarget;
                _lastSelectionTime = DateTime.UtcNow;

                var weight = CalculateFinalWeight(bestTarget);
                EventBus.Instance.Publish(new TargetAcquiredEvent(
                    bestTarget,
                    Vector2.Distance(playerPos, bestTarget.GridPosNum),
                    bestTarget.GridPosNum,
                    weight));

                if (oldTarget != null)
                {
                    EventBus.Instance.Publish(new TargetLostEvent(
                        oldTarget,
                        "Switched to higher priority target",
                        oldTarget.GridPosNum));
                }
            }
        }

        public List<Entity> GetValidTargets(ActiveSkill skill)
        {
            var entities = _entityScanner.GetEntitiesInRange(_maxTargetDistance);
            var losType = LineOfSight.Parse(skill.LineOfSightType.Value);

            return entities.Where(e =>
            {
                if (!IsTargetValid(e)) return false;
                var hasLos = _entityScanner.GetEntityLineOfSight(e, losType);
                return hasLos.HasValue && hasLos.Value;
            }).ToList();
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

            return newWeight > currentWeight + _minWeightDifferenceForSwitch;
        }

        private void HandleTargetLost()
        {
            if (_currentTarget != null)
            {
                EventBus.Instance.Publish(new TargetLostEvent(
                    _currentTarget,
                    "Target is no longer valid or in range",
                    _currentTarget.GridPosNum));
                _currentTarget = null;
            }
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
            _settingsMonitor.OnSettingsChanged -= () => Configure();
            _settingsMonitor.Dispose();
        }
    }
}