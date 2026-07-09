using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using FollowHer.Core.Combat.Skills;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using FollowHer.Utils;

namespace FollowHer.Features.Targeting.EntityInformation
{
    public class EntityScanner
    {
        private readonly GameController _gameController;
        private readonly LineOfSight _lineOfSight;
        private readonly HashSet<Entity> _trackedEntities;
        private readonly Dictionary<Entity, float> _entityDistances;
        private readonly Dictionary<Entity, Dictionary<LineOfSightDataType, bool>> _entityLineOfSight;
        private readonly Dictionary<Entity, DateTime> _lastSeenTimes;
        private readonly object _lock = new();

        private Vector2 _lastPlayerPosition;
        private float _maxScanRange;
        private DateTime _lastFullScan;

        public EntityScanner(GameController gameController, LineOfSight lineOfSight)
        {
            _gameController = gameController;
            _lineOfSight = lineOfSight;
            _trackedEntities = new HashSet<Entity>();
            _entityDistances = new Dictionary<Entity, float>();
            _entityLineOfSight = new Dictionary<Entity, Dictionary<LineOfSightDataType, bool>>();
            _lastSeenTimes = new Dictionary<Entity, DateTime>();
            _maxScanRange = 100f;
            _lastFullScan = DateTime.MinValue;

            var eventBus = EventBus.Instance;
            eventBus.Subscribe<AreaChangeEvent>(HandleAreaChange);
        }

        private void HandleAreaChange(AreaChangeEvent evt)
        {
            Clear();
        }

        public void SetScanRange(float range)
        {
            _maxScanRange = range;
        }


        public void Scan()
        {
            if (_gameController?.Player == null) return;

            var currentTime = DateTime.UtcNow;
            var playerPosition = _gameController.Player.GridPosNum;

            if (ShouldPerformFullScan(playerPosition, currentTime))
            {
                PerformFullScan(playerPosition, currentTime, new List<LineOfSightDataType> { LineOfSightDataType.Terrain });
                _lastPlayerPosition = playerPosition;
                _lastFullScan = currentTime;
            }
            else
            {
                UpdateTrackedEntities(playerPosition, currentTime, new List<LineOfSightDataType> { LineOfSightDataType.Terrain });
            }

            CleanupStaleEntities(currentTime);
        }

        public void Scan(IReadOnlyCollection<ActiveSkill> skills)
        {
            if (_gameController?.Player == null) return;

            var currentTime = DateTime.UtcNow;
            var playerPosition = _gameController.Player.GridPosNum;

            var requiredLosTypes = skills
                .Select(s => LineOfSight.Parse(s.LineOfSightType.Value))
                .Distinct()
                .ToList();

            if (ShouldPerformFullScan(playerPosition, currentTime))
            {
                PerformFullScan(playerPosition, currentTime, requiredLosTypes);
                _lastPlayerPosition = playerPosition;
                _lastFullScan = currentTime;
            }
            else
            {
                UpdateTrackedEntities(playerPosition, currentTime, requiredLosTypes);
            }

            CleanupStaleEntities(currentTime);
        }

        private bool ShouldPerformFullScan(Vector2 currentPlayerPos, DateTime currentTime)
        {
            if (_lastFullScan == DateTime.MinValue) return true;
            if ((currentTime - _lastFullScan).TotalMilliseconds > 100) return true;
            if (_lastPlayerPosition == Vector2.Zero) return true;
            return Vector2.Distance(_lastPlayerPosition, currentPlayerPos) > 10f;
        }

        private void PerformFullScan(Vector2 playerPosition, DateTime currentTime, List<LineOfSightDataType> losTypes)
        {
            if (!_gameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var validMonsters))
            {
                validMonsters = new List<Entity>();
            }
            
            var newEntities = new HashSet<Entity>();

            foreach (var entity in validMonsters)
            {
                if (!IsValidTarget(entity)) continue;

                var distance = Vector2.Distance(playerPosition, entity.GridPosNum);
                if (distance > _maxScanRange) continue;

                newEntities.Add(entity);
                UpdateEntityTracking(entity, distance, currentTime, playerPosition, losTypes);
            }

            var removedEntities = _trackedEntities.Except(newEntities).ToList();
            foreach (var entity in removedEntities)
            {
                RemoveEntity(entity);
            }

            lock (_lock)
            {
                _trackedEntities.Clear();
                _trackedEntities.UnionWith(newEntities);
            }
        }

        private void UpdateTrackedEntities(Vector2 playerPosition, DateTime currentTime, List<LineOfSightDataType> losTypes)
        {
            var invalidEntities = new List<Entity>();

            lock (_lock)
            {
                foreach (var entity in _trackedEntities)
                {
                    if (!IsEntityValid(entity))
                    {
                        invalidEntities.Add(entity);
                        continue;
                    }

                    var distance = Vector2.Distance(playerPosition, entity.GridPosNum);
                    UpdateEntityTracking(entity, distance, currentTime, playerPosition, losTypes);
                }

                foreach (var entity in invalidEntities)
                {
                    RemoveEntity(entity);
                }
            }
        }

        private void UpdateEntityTracking(Entity entity, float distance, DateTime currentTime, Vector2 playerPosition, IReadOnlyCollection<LineOfSightDataType> losTypes)
        {
            bool isNewEntity = !_entityDistances.ContainsKey(entity);
            float? oldDistance = isNewEntity ? null : _entityDistances[entity];

            bool losChanged = false;
            if (!_entityLineOfSight.TryGetValue(entity, out var currentLoSStates))
            {
                currentLoSStates = new Dictionary<LineOfSightDataType, bool>();
                _entityLineOfSight[entity] = currentLoSStates;
            }

            foreach (var losType in losTypes)
            {
                var hadLineOfSight = currentLoSStates.TryGetValue(losType, out var previousLoS) && previousLoS;
                var currentLoS = CheckLineOfSight(entity, playerPosition, losType);
                currentLoSStates[losType] = currentLoS;

                if (hadLineOfSight != currentLoS)
                {
                    losChanged = true;
                    EventBus.Instance.Publish(new TargetInLineOfSightEvent(entity, currentLoS, entity.GridPosNum, distance));
                }
            }

            lock (_lock)
            {
                _entityDistances[entity] = distance;
                _lastSeenTimes[entity] = currentTime;
            }

            if (isNewEntity)
            {
                EventBus.Instance.Publish(new EntityDiscoveredEvent
                {
                    Entity = entity,
                    Distance = distance
                });
            }
            else if (oldDistance.HasValue && (Math.Abs(oldDistance.Value - distance) > 5f || losChanged))
            {
                EventBus.Instance.Publish(new TargetStateChangedEvent(
                    entity,
                    entity.IsAlive,
                    entity.IsTargetable,
                    GetHealthPercentage(entity),
                    distance));
            }
        }

        private void RemoveEntity(Entity entity)
        {
            lock (_lock)
            {
                _trackedEntities.Remove(entity);
                _entityDistances.Remove(entity);
                _entityLineOfSight.Remove(entity);
                _lastSeenTimes.Remove(entity);
            }

            if (entity.GridPosNum != Vector2.Zero)
            {
                EventBus.Instance.Publish(new TargetLostEvent(
                    entity,
                    "Entity no longer valid",
                    entity.GridPosNum));
            }
        }

        private void CleanupStaleEntities(DateTime currentTime)
        {
            var staleEntities = new List<Entity>();

            lock (_lock)
            {
                foreach (var kvp in _lastSeenTimes)
                {
                    if ((currentTime - kvp.Value).TotalSeconds > 5)
                    {
                        staleEntities.Add(kvp.Key);
                    }
                }

                foreach (var entity in staleEntities)
                {
                    RemoveEntity(entity);
                }
            }
        }

        private bool IsValidTarget(Entity entity)
        {
            if (entity == null) return false;

            return entity.IsValid &&
                   entity.IsAlive &&
                   !entity.IsDead &&
                   entity.IsTargetable &&
                   !entity.IsHidden &&
                   entity.IsHostile &&
                   !IsImmuneToAllDamage(entity);
        }

        private bool IsEntityValid(Entity entity)
        {
            if (entity == null) return false;
            if (!entity.IsValid) return false;
            if (entity.Address == 0) return false;

            try
            {
                var pos = entity.GridPosNum;
                var isAlive = entity.IsAlive;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsImmuneToAllDamage(Entity entity)
        {
            try
            {
                if (entity.Stats == null || entity.Stats.Count == 0)
                    return false;

                if (!entity.Stats.TryGetValue(GameStat.CannotBeDamaged, out var value))
                    return false;

                return value == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private float GetHealthPercentage(Entity entity)
        {
            try
            {
                if (entity?.GetComponent<Life>() is not Life life)
                    return 0f;

                return life.HPPercentage;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private bool CheckLineOfSight(Entity entity, Vector2 playerPos, LineOfSightDataType losType)
        {
            if (entity == null) return false;

            try
            {
                return _lineOfSight.HasLineOfSight(playerPos, entity.GridPosNum, losType);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public IEnumerable<Entity> GetEntitiesInRange(float range)
        {
            lock (_lock)
            {
                return _entityDistances
                    .Where(kvp => kvp.Value <= range)
                    .Select(kvp => kvp.Key)
                    .ToList();
            }
        }

        public float? GetEntityDistance(Entity entity)
        {
            lock (_lock)
            {
                return _entityDistances.TryGetValue(entity, out float distance) ? distance : null;
            }
        }

        public bool? GetEntityLineOfSight(Entity entity, LineOfSightDataType losType)
        {
            lock (_lock)
            {
                if (_entityLineOfSight.TryGetValue(entity, out var losStates))
                {
                    if (losStates.TryGetValue(losType, out bool los))
                    {
                        return los;
                    }
                }
                return null;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _trackedEntities.Clear();
                _entityDistances.Clear();
                _entityLineOfSight.Clear();
                _lastSeenTimes.Clear();
                _lastPlayerPosition = Vector2.Zero;
                _lastFullScan = DateTime.MinValue;
            }
        }
    }
}