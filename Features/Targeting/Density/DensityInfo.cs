using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore.PoEMemory.MemoryObjects;

namespace FollowHer.Features.Targeting.Density
{
    public class DensityInfo
    {
        public Vector2 Center { get; }
        public float Radius { get; }
        public List<Entity> Entities { get; }
        public float DensityScore { get; }
        public float AverageDistance { get; }

        private DensityInfo(
            Vector2 center,
            float radius,
            List<Entity> entities,
            float densityScore,
            float averageDistance)
        {
            Center = center;
            Radius = radius;
            Entities = entities;
            DensityScore = densityScore;
            AverageDistance = averageDistance;
        }

        public static DensityInfo Calculate(
            IEnumerable<Entity> entities,
            float maxRadius,
            float minEntities = 2)
        {
            var entityList = new List<Entity>(entities);
            if (entityList.Count < minEntities)
            {
                return null;
            }

            var (center, averageDistance) = CalculateDensityCenter(entityList);
            var radius = CalculateEffectiveRadius(entityList, center, maxRadius);
            var densityScore = CalculateDensityScore(entityList.Count, radius, maxRadius);

            return new DensityInfo(
                center,
                radius,
                entityList,
                densityScore,
                averageDistance
            );
        }

        private static (Vector2 center, float averageDistance) CalculateDensityCenter(List<Entity> entities)
        {
            var center = Vector2.Zero;
            var totalWeight = 0f;

            foreach (var entity in entities)
            {
                float weight = 1.0f;
                center += entity.GridPosNum * weight;
                totalWeight += weight;
            }

            if (totalWeight > 0)
            {
                center /= totalWeight;
            }

            float totalDistance = 0f;
            foreach (var entity in entities)
            {
                totalDistance += Vector2.Distance(center, entity.GridPosNum);
            }

            float averageDistance = totalDistance / entities.Count;

            return (center, averageDistance);
        }

        private static float CalculateEffectiveRadius(List<Entity> entities, Vector2 center, float maxRadius)
        {
            float maxDistanceSq = 0f;

            foreach (var entity in entities)
            {
                var distanceSq = Vector2.DistanceSquared(center, entity.GridPosNum);
                maxDistanceSq = Math.Max(maxDistanceSq, distanceSq);
            }

            return Math.Min(MathF.Sqrt(maxDistanceSq), maxRadius);
        }

        private static float CalculateDensityScore(int entityCount, float radius, float maxRadius)
        {
            float entityScore = entityCount / (entityCount + 5f);
            float radiusScore = 1f - (radius / maxRadius);
            float densityScore = (entityScore * 0.7f) + (radiusScore * 0.3f);

            return densityScore;
        }

        public bool ContainsPosition(Vector2 position)
        {
            return Vector2.Distance(Center, position) <= Radius;
        }

        public bool ContainsEntity(Entity entity)
        {
            return ContainsPosition(entity.GridPosNum);
        }

        public float GetDistanceFrom(Vector2 position)
        {
            return Vector2.Distance(Center, position);
        }

        public float GetDistanceFromEdge(Vector2 position)
        {
            var distanceFromCenter = Vector2.Distance(Center, position);
            return Math.Max(0f, distanceFromCenter - Radius);
        }

        public IEnumerable<Entity> GetEntitiesInRange(float range)
        {
            var results = new List<Entity>();
            var rangeSq = range * range;

            foreach (var entity in Entities)
            {
                if (Vector2.DistanceSquared(Center, entity.GridPosNum) <= rangeSq)
                {
                    results.Add(entity);
                }
            }

            return results;
        }

        public DensityMetrics GetMetrics()
        {
            return new DensityMetrics
            {
                EntityCount = Entities.Count,
                Radius = Radius,
                DensityScore = DensityScore,
                AverageDistance = AverageDistance,
                Area = MathF.PI * Radius * Radius,
                EntitiesPerUnit = Entities.Count / (MathF.PI * Radius * Radius)
            };
        }
    }

    public class DensityMetrics
    {
        public int EntityCount { get; set; }
        public float Radius { get; set; }
        public float DensityScore { get; set; }
        public float AverageDistance { get; set; }
        public float Area { get; set; }
        public float EntitiesPerUnit { get; set; }
    }
}