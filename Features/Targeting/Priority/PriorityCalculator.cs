using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

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

        private float _normalRarityMultiplier = 1.0f;
        private float _magicRarityMultiplier = 2.0f;
        private float _rareRarityMultiplier = 3.0f;
        private float _uniqueRarityMultiplier = 4.0f;

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
            bool preferHigherHealth,
            float normalRarityMultiplier = 1.0f,
            float magicRarityMultiplier = 2.0f,
            float rareRarityMultiplier = 3.0f,
            float uniqueRarityMultiplier = 4.0f)
        {
            lock (_lock)
            {
                _distanceWeight = distanceWeight;
                _healthWeight = healthWeight;
                _rarityWeight = rarityWeight;
                _maxTargetDistance = maxTargetDistance;
                _preferHigherHealth = preferHigherHealth;
                _normalRarityMultiplier = normalRarityMultiplier;
                _magicRarityMultiplier = magicRarityMultiplier;
                _rareRarityMultiplier = rareRarityMultiplier;
                _uniqueRarityMultiplier = uniqueRarityMultiplier;
            }
        }

        public void UpdatePriorities(IEnumerable<Entity> entities)
        {
            if (_gameController?.Player == null) return;

            var playerPos = _gameController.Player.GridPosNum;
            _frameCounter++;

            foreach (var entity in entities)
            {
                if (!IsEntityValid(entity)) continue;

                var newWeight = CalculateWeight(entity, playerPos);

                lock (_lock)
                {
                    _currentWeights[entity] = newWeight;
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
            float distanceWeight, healthWeight, rarityWeight, maxTargetDistance;
            bool preferHigherHealth;
            float normalMult, magicMult, rareMult, uniqueMult;

            lock (_lock)
            {
                distanceWeight = _distanceWeight;
                healthWeight = _healthWeight;
                rarityWeight = _rarityWeight;
                maxTargetDistance = _maxTargetDistance;
                preferHigherHealth = _preferHigherHealth;
                normalMult = _normalRarityMultiplier;
                magicMult = _magicRarityMultiplier;
                rareMult = _rareRarityMultiplier;
                uniqueMult = _uniqueRarityMultiplier;
            }

            var distance = Vector2.Distance(playerPos, entity.GridPosNum);
            if (distance > maxTargetDistance) return 0f;

            var weight = 0f;

            var distanceNormalized = 1f - (distance / maxTargetDistance);
            weight += distanceNormalized * distanceNormalized * distanceWeight;

            weight += CalculateHealthPriority(entity, distanceNormalized, healthWeight, preferHigherHealth);
            weight += CalculateRarityPriority(entity, distanceNormalized, rarityWeight, normalMult, magicMult, rareMult, uniqueMult);

            return weight;
        }

        private float CalculateHealthPriority(Entity entity, float distanceFactor, float healthWeight, bool preferHigherHealth)
        {
            var life = GetLifeComponent(entity);
            if (life == null) return 0f;

            var totalHealthPercent = life.HPPercentage + life.ESPercentage;
            var healthPriority = preferHigherHealth ? totalHealthPercent : (1f - totalHealthPercent);

            return healthPriority * healthWeight * distanceFactor;
        }

        private float CalculateRarityPriority(Entity entity, float distanceFactor, float rarityWeight,
            float normalMult, float magicMult, float rareMult, float uniqueMult)
        {
            var rarity = GetMonsterRarity(entity);

            var rarityMultiplier = rarity switch
            {
                MonsterRarity.Unique => uniqueMult,
                MonsterRarity.Rare => rareMult,
                MonsterRarity.Magic => magicMult,
                _ => normalMult
            };

            return rarityMultiplier * rarityWeight * distanceFactor;
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