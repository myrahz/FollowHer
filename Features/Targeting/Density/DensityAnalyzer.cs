using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FollowHer.Features.Targeting.EntityInformation;
using FollowHer.Utils;

namespace FollowHer.Features.Targeting.Density
{
    public class DensityAnalyzer
    {
        private readonly GameController _gameController;
        private readonly EntityScanner _entityScanner;
        private readonly Dictionary<Vector2, DensityInfo> _densityClusters;
        private readonly object _lock = new();

        private float _maxRadius = 50f;
        private float _minEntities = 3;
        private bool _requireLineOfSight;
        private DateTime _lastUpdate;

        public DensityAnalyzer(GameController gameController, EntityScanner entityScanner)
        {
            _gameController = gameController;
            _entityScanner = entityScanner;
            _densityClusters = new Dictionary<Vector2, DensityInfo>();
            _lastUpdate = DateTime.MinValue;
        }

        public void Configure(float maxRadius, float minEntities, bool requireLineOfSight)
        {
            lock (_lock)
            {
                _maxRadius = maxRadius;
                _minEntities = minEntities;
                _requireLineOfSight = requireLineOfSight;
            }
        }

        public void Update(IEnumerable<Entity> entities, IReadOnlyCollection<LineOfSightDataType> losTypes)
        {
            if (_gameController?.Player == null) return;

            var currentTime = DateTime.Now;
            if ((currentTime - _lastUpdate).TotalMilliseconds < 100) return;

            try
            {
                float maxRadius, minEntities;
                lock (_lock)
                {
                    maxRadius = _maxRadius;
                    minEntities = _minEntities;
                }

                var playerPos = _gameController.Player.GridPosNum;
                var validEntities = FilterEntities(entities, playerPos, losTypes);
                var clusters = FindDensityClusters(validEntities, maxRadius, minEntities);

                lock (_lock)
                {
                    _densityClusters.Clear();
                    foreach (var cluster in clusters)
                    {
                        _densityClusters[cluster.Center] = cluster;
                    }
                }

                _lastUpdate = currentTime;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[DensityAnalyzer] Error updating densities: {ex.Message}");
            }
        }

        private IEnumerable<Entity> FilterEntities(IEnumerable<Entity> entities, Vector2 playerPos, IReadOnlyCollection<LineOfSightDataType> losTypes)
        {
            bool requireLineOfSight;
            float maxRadius;
            lock (_lock)
            {
                requireLineOfSight = _requireLineOfSight;
                maxRadius = _maxRadius;
            }

            return entities.Where(entity =>
            {
                if (entity == null || !entity.IsValid) return false;
                if (!entity.IsAlive || entity.IsDead) return false;
                if (!entity.IsTargetable || entity.IsHidden) return false;

                var distance = Vector2.Distance(playerPos, entity.GridPosNum);
                if (distance > maxRadius * 2) return false;

                if (requireLineOfSight)
                {
                    // Reuse EntityScanner's already-computed LOS cache instead of re-raycasting.
                    if (!losTypes.Any(losType => _entityScanner.GetEntityLineOfSight(entity, losType) == true))
                        return false;
                }

                return true;
            });
        }

        private List<DensityInfo> FindDensityClusters(IEnumerable<Entity> entities, float maxRadius, float minEntities)
        {
            var remainingEntities = new HashSet<Entity>(entities);
            var clusters = new List<DensityInfo>();

            while (remainingEntities.Count > 0)
            {
                var seedEntity = remainingEntities.First();
                var clusterEntities = GetEntitiesInRange(seedEntity, remainingEntities, maxRadius);

                if (clusterEntities.Count >= minEntities)
                {
                    var density = DensityInfo.Calculate(clusterEntities, maxRadius, minEntities);
                    if (density != null)
                    {
                        clusters.Add(density);
                    }
                }

                foreach (var entity in clusterEntities)
                {
                    remainingEntities.Remove(entity);
                }
            }

            return clusters.OrderByDescending(c => c.DensityScore).ToList();
        }

        private List<Entity> GetEntitiesInRange(Entity center, IEnumerable<Entity> entities, float maxRadius)
        {
            var inRange = new List<Entity>();
            var centerPos = center.GridPosNum;

            foreach (var entity in entities)
            {
                var distance = Vector2.Distance(centerPos, entity.GridPosNum);
                if (distance <= maxRadius)
                {
                    inRange.Add(entity);
                }
            }

            return inRange;
        }

        public DensityInfo GetDensityAtPosition(Vector2 position)
        {
            lock (_lock)
            {
                return _densityClusters.Values
                    .FirstOrDefault(d => d.ContainsPosition(position));
            }
        }

        public IEnumerable<DensityInfo> GetAllDensities()
        {
            lock (_lock)
            {
                return _densityClusters.Values.ToList();
            }
        }

        public DensityInfo GetHighestDensity()
        {
            lock (_lock)
            {
                return _densityClusters.Values
                    .OrderByDescending(d => d.DensityScore)
                    .FirstOrDefault();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _densityClusters.Clear();
                _lastUpdate = DateTime.MinValue;
            }
        }
    }
}