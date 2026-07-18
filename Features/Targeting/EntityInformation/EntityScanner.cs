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
using FollowHer.Utils;

namespace FollowHer.Features.Targeting.EntityInformation
{
    public class EntityScanner
    {
        private const int IncrementalUpdateIntervalMs = 50;

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
        private DateTime _lastIncrementalUpdate;

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
            _lastIncrementalUpdate = DateTime.MinValue;

            var eventBus = EventBus.Instance;
            eventBus.Subscribe<AreaChangeEvent>(HandleAreaChange);
        }

        public void Dispose()
        {
            EventBus.Instance.Unsubscribe<AreaChangeEvent>(HandleAreaChange);
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
                _lastIncrementalUpdate = currentTime;
            }
            else if ((currentTime - _lastIncrementalUpdate).TotalMilliseconds >= IncrementalUpdateIntervalMs)
            {
                UpdateTrackedEntities(playerPosition, currentTime, new List<LineOfSightDataType> { LineOfSightDataType.Terrain });
                _lastIncrementalUpdate = currentTime;
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
                _lastIncrementalUpdate = currentTime;
            }
            else if ((currentTime - _lastIncrementalUpdate).TotalMilliseconds >= IncrementalUpdateIntervalMs)
            {
                UpdateTrackedEntities(playerPosition, currentTime, requiredLosTypes);
                _lastIncrementalUpdate = currentTime;
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

            List<Entity> removedEntities;
            lock (_lock)
            {
                removedEntities = _trackedEntities.Except(newEntities).ToList();
                _trackedEntities.Clear();
                _trackedEntities.UnionWith(newEntities);
            }

            foreach (var entity in removedEntities)
            {
                RemoveEntity(entity);
            }
        }

        private void UpdateTrackedEntities(Vector2 playerPosition, DateTime currentTime, List<LineOfSightDataType> losTypes)
        {
            List<Entity> trackedSnapshot;
            lock (_lock)
            {
                trackedSnapshot = _trackedEntities.ToList();
            }

            var invalidEntities = new List<Entity>();

            foreach (var entity in trackedSnapshot)
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

        private void UpdateEntityTracking(Entity entity, float distance, DateTime currentTime, Vector2 playerPosition, IReadOnlyCollection<LineOfSightDataType> losTypes)
        {
            Dictionary<LineOfSightDataType, bool> currentLoSStates;
            lock (_lock)
            {
                if (!_entityLineOfSight.TryGetValue(entity, out currentLoSStates))
                {
                    currentLoSStates = new Dictionary<LineOfSightDataType, bool>();
                    _entityLineOfSight[entity] = currentLoSStates;
                }
            }

            var losResults = new Dictionary<LineOfSightDataType, bool>();
            foreach (var losType in losTypes)
            {
                losResults[losType] = CheckLineOfSight(entity, playerPosition, losType);
            }

            lock (_lock)
            {
                foreach (var (losType, value) in losResults)
                {
                    currentLoSStates[losType] = value;
                }

                _entityDistances[entity] = distance;
                _lastSeenTimes[entity] = currentTime;
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
        }

        private void CleanupStaleEntities(DateTime currentTime)
        {
            List<Entity> staleEntities;

            lock (_lock)
            {
                staleEntities = _lastSeenTimes
                    .Where(kvp => (currentTime - kvp.Value).TotalSeconds > 5)
                    .Select(kvp => kvp.Key)
                    .ToList();
            }

            foreach (var entity in staleEntities)
            {
                RemoveEntity(entity);
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
                _lastIncrementalUpdate = DateTime.MinValue;
            }
        }
    }
}
