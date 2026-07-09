using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FollowHer.Core.Combat;
using FollowHer.Features.Targeting.Density;
using FollowHer.Features.Targeting.EntityInformation;

namespace FollowHer.Core.Events
{
    public class EventBus
    {
        private static EventBus _instance;
        private readonly Dictionary<Type, List<Delegate>> _subscribers;
        private readonly object _lock = new();

        private EventBus()
        {
            _subscribers = new Dictionary<Type, List<Delegate>>();
        }

        public static EventBus Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new EventBus();
                }
                return _instance;
            }
        }

        public void Subscribe<T>(Action<T> handler)
        {
            lock (_lock)
            {
                var eventType = typeof(T);
                if (!_subscribers.ContainsKey(eventType))
                {
                    _subscribers[eventType] = new List<Delegate>();
                }
                _subscribers[eventType].Add(handler);
            }
        }

        public void Unsubscribe<T>(Action<T> handler)
        {
            lock (_lock)
            {
                var eventType = typeof(T);
                if (_subscribers.ContainsKey(eventType))
                {
                    _subscribers[eventType].Remove(handler);
                    if (_subscribers[eventType].Count == 0)
                    {
                        _subscribers.Remove(eventType);
                    }
                }
            }
        }

        public void Publish<T>(T eventData)
        {
            List<Delegate> handlers;
            lock (_lock)
            {
                if (!_subscribers.ContainsKey(typeof(T)))
                    return;

                handlers = new List<Delegate>(_subscribers[typeof(T)]);
            }

            foreach (var handler in handlers)
            {
                try
                {
                    ((Action<T>)handler)(eventData);
                }
                catch (Exception ex)
                {
                    DebugWindow.LogError($"[EventBus] Error publishing event {typeof(T).Name}: {ex.Message}");
                }
            }
        }

        public async Task PublishAsync<T>(T eventData)
        {
            List<Delegate> handlers;
            lock (_lock)
            {
                if (!_subscribers.ContainsKey(typeof(T)))
                    return;

                handlers = new List<Delegate>(_subscribers[typeof(T)]);
            }

            var tasks = new List<Task>();
            foreach (var handler in handlers)
            {
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        ((Action<T>)handler)(eventData);
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.LogError($"[EventBus] Error publishing async event {typeof(T).Name}: {ex.Message}");
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        public void Clear()
        {
            lock (_lock)
            {
                _subscribers.Clear();
            }
        }
    }

    public class AreaChangeEvent
    {
        public AreaInstance NewArea { get; set; }
    }

    public class SkillUsedEvent
    {
        public string SkillName { get; set; }
        public DateTime UseTime { get; set; }
        public EntityInfo Target { get; set; }
    }

    public class CombatStateChangedEvent
    {
        public bool IsInCombat { get; set; }
        public EntityInfo CurrentTarget { get; set; }
    }

    public class EntityDiscoveredEvent
    {
        public Entity Entity { get; set; }
        public float Distance { get; set; }
    }

    public class DensityUpdatedEvent
    {
        public List<DensityInfo> Densities { get; set; }
    }

    public class WeightUpdatedEvent
    {
        public EntityInfo Entity { get; set; }
        public float OldWeight { get; set; }
        public float NewWeight { get; set; }
    }

    public class CombatRoutineChangedEvent
    {
        public string OldRoutineName { get; set; }
        public string NewRoutineName { get; set; }
        public RoutineBase NewRoutine { get; set; }
    }
}