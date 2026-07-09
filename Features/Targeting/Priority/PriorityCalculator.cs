using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;

namespace FollowHer.Features.Targeting.Priority
{
    public class PriorityCalculator
    {
        private readonly GameController _gameController;
        private readonly Dictionary<Entity, float> _currentWeights;
        private readonly Dictionary<Entity, Life> _lifeCache;
        private readonly Dictionary<Entity, MonsterRarity> _rarityCache;
        private readonly object _lock = new();

        private float _distanceWeight = 2.0f;
        private float _healthWeight = 1.0f;
        private float _rarityWeight = 1.0f;
        private float _maxTargetDistance = 100f;
        private bool _preferHigherHealth;

        private const int CACHE_CLEANUP_INTERVAL = 120;
        private int _frameCounter;

        public PriorityCalculator(GameController gameController)
        {
            _gameController = gameController;
            _currentWeights = new Dictionary<Entity, float>();
            _lifeCache = new Dictionary<Entity, Life>();
            _rarityCache = new Dictionary<Entity, MonsterRarity>();
        }

        public void Configure(
            float distanceWeight,
            float healthWeight,
            float rarityWeight,
            float maxTargetDistance,
            bool preferHigherHealth)
        {
            _distanceWeight = distanceWeight;
            _healthWeight = healthWeight;
            _rarityWeight = rarityWeight;
            _maxTargetDistance = maxTargetDistance;
            _preferHigherHealth = preferHigherHealth;
        }

        public void UpdatePriorities(IEnumerable<Entity> entities)
        {
            if (_gameController?.Player == null) return;

            var playerPos = _gameController.Player.GridPosNum;
            _frameCounter++;

            foreach (var entity in entities)
            {
                if (!IsEntityValid(entity)) continue;

                var oldWeight = GetCurrentWeight(entity);
                var newWeight = CalculateWeight(entity, playerPos);

                if (Math.Abs(oldWeight - newWeight) > 0.1f)
                {
                    lock (_lock)
                    {
                        _currentWeights[entity] = newWeight;
                    }

                    EventBus.Instance.Publish(new TargetPriorityChangedEvent(
                        entity,
                        oldWeight,
                        newWeight,
                        Vector2.Distance(playerPos, entity.GridPosNum)));
                }
            }

            if (_frameCounter >= CACHE_CLEANUP_INTERVAL)
            {
                CleanupCaches();
                _frameCounter = 0;
            }
        }

        private float CalculateWeight(Entity entity, Vector2 playerPos)
        {
            var distance = Vector2.Distance(playerPos, entity.GridPosNum);
            if (distance > _maxTargetDistance) return 0f;

            var weight = 0f;

            var distanceNormalized = 1f - (distance / _maxTargetDistance);
            weight += distanceNormalized * distanceNormalized * _distanceWeight;

            weight += CalculateHealthPriority(entity, distanceNormalized);
            weight += CalculateRarityPriority(entity, distanceNormalized);

            return weight;
        }

        private float CalculateHealthPriority(Entity entity, float distanceFactor)
        {
            var life = GetLifeComponent(entity);
            if (life == null) return 0f;

            var totalHealthPercent = life.HPPercentage + life.ESPercentage;
            var healthPriority = _preferHigherHealth ? totalHealthPercent : (1f - totalHealthPercent);

            return healthPriority * _healthWeight * distanceFactor;
        }

        private float CalculateRarityPriority(Entity entity, float distanceFactor)
        {
            var rarity = GetMonsterRarity(entity);

            var rarityMultiplier = rarity switch
            {
                MonsterRarity.Unique => 4.0f,
                MonsterRarity.Rare => 3.0f,
                MonsterRarity.Magic => 2.0f,
                _ => 1.0f
            };

            return rarityMultiplier * _rarityWeight * distanceFactor;
        }

        private Life GetLifeComponent(Entity entity)
        {
            lock (_lock)
            {
                if (!_lifeCache.TryGetValue(entity, out var life))
                {
                    life = entity.GetComponent<Life>();
                    if (life != null)
                    {
                        _lifeCache[entity] = life;
                    }
                }
                return life;
            }
        }

        private MonsterRarity GetMonsterRarity(Entity entity)
        {
            lock (_lock)
            {
                if (!_rarityCache.TryGetValue(entity, out var rarity))
                {
                    rarity = entity.GetComponent<ObjectMagicProperties>()?.Rarity ?? MonsterRarity.White;
                    _rarityCache[entity] = rarity;
                }
                return rarity;
            }
        }

        private float GetCurrentWeight(Entity entity)
        {
            lock (_lock)
            {
                return _currentWeights.TryGetValue(entity, out var weight) ? weight : 0f;
            }
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

        private void CleanupCaches()
        {
            lock (_lock)
            {
                var invalidEntities = new List<Entity>();

                foreach (var entity in _currentWeights.Keys)
                {
                    if (!IsEntityValid(entity))
                    {
                        invalidEntities.Add(entity);
                    }
                }

                foreach (var entity in invalidEntities)
                {
                    _currentWeights.Remove(entity);
                    _lifeCache.Remove(entity);
                    _rarityCache.Remove(entity);
                }
            }
        }

        public float? GetEntityWeight(Entity entity)
        {
            lock (_lock)
            {
                return _currentWeights.TryGetValue(entity, out float weight) ? weight : null;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _currentWeights.Clear();
                _lifeCache.Clear();
                _rarityCache.Clear();
                _frameCounter = 0;
            }
        }
    }
}