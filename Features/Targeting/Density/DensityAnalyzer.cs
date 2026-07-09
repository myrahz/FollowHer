using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FollowHer.Core.Events;
using FollowHer.Features.Targeting.Density;
using FollowHer.Utils;

namespace FollowHer.Features.Targeting.Density
{
    public class DensityAnalyzer
    {
        private readonly GameController _gameController;
        private readonly LineOfSight _lineOfSight;
        private readonly Dictionary<Vector2, DensityInfo> _densityClusters;
        private readonly object _lock = new();

        private float _maxRadius = 50f;
        private float _minEntities = 3;
        private bool _requireLineOfSight;
        private DateTime _lastUpdate;

        public DensityAnalyzer(GameController gameController, LineOfSight lineOfSight)
        {
            _gameController = gameController;
            _lineOfSight = lineOfSight;
            _densityClusters = new Dictionary<Vector2, DensityInfo>();
            _lastUpdate = DateTime.MinValue;
        }

        public void Configure(float maxRadius, float minEntities, bool requireLineOfSight)
        {
            _maxRadius = maxRadius;
            _minEntities = minEntities;
            _requireLineOfSight = requireLineOfSight;
        }

        public void Update(IEnumerable<Entity> entities, IReadOnlyCollection<LineOfSightDataType> losTypes)
        {
            if (_gameController?.Player == null) return;

            var currentTime = DateTime.Now;
            if ((currentTime - _lastUpdate).TotalMilliseconds < 100) return;

            try
            {
                var playerPos = _gameController.Player.GridPosNum;
                var validEntities = FilterEntities(entities, playerPos, losTypes);
                var clusters = FindDensityClusters(validEntities, playerPos);

                lock (_lock)
                {
                    _densityClusters.Clear();
                    foreach (var cluster in clusters)
                    {
                        _densityClusters[cluster.Center] = cluster;
                    }
                }

                EventBus.Instance.Publish(new DensityUpdatedEvent
                {
                    Densities = clusters.ToList()
                });

                _lastUpdate = currentTime;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[DensityAnalyzer] Error updating densities: {ex.Message}");
            }
        }

        private IEnumerable<Entity> FilterEntities(IEnumerable<Entity> entities, Vector2 playerPos, IReadOnlyCollection<LineOfSightDataType> losTypes)
        {
            return entities.Where(entity =>
            {
                if (entity == null || !entity.IsValid) return false;
                if (!entity.IsAlive || entity.IsDead) return false;
                if (!entity.IsTargetable || entity.IsHidden) return false;

                var distance = Vector2.Distance(playerPos, entity.GridPosNum);
                if (distance > _maxRadius * 2) return false;

                if (_requireLineOfSight)
                {
                    if (!losTypes.Any(losType => _lineOfSight.HasLineOfSight(playerPos, entity.GridPosNum, losType)))
                        return false;
                }

                return true;
            });
        }

        private List<DensityInfo> FindDensityClusters(IEnumerable<Entity> entities, Vector2 playerPos)
        {
            var remainingEntities = new HashSet<Entity>(entities);
            var clusters = new List<DensityInfo>();

            while (remainingEntities.Count > 0)
            {
                var seedEntity = remainingEntities.First();
                var clusterEntities = GetEntitiesInRange(seedEntity, remainingEntities);

                if (clusterEntities.Count >= _minEntities)
                {
                    var density = DensityInfo.Calculate(clusterEntities, _maxRadius, _minEntities);
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

        private List<Entity> GetEntitiesInRange(Entity center, IEnumerable<Entity> entities)
        {
            var inRange = new List<Entity>();
            var centerPos = center.GridPosNum;

            foreach (var entity in entities)
            {
                var distance = Vector2.Distance(centerPos, entity.GridPosNum);
                if (distance <= _maxRadius)
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