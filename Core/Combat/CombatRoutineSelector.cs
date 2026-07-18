using ExileCore;
using FollowHer.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FollowHer.Core.Combat;

namespace FollowHer.Core.Combat
{
    public class CombatRoutineSelector
    {
        private readonly GameController _gameController;
        private readonly Dictionary<string, Type> _routineTypes;

        public CombatRoutineSelector(GameController gameController)
        {
            _gameController = gameController;
            _routineTypes = DiscoverRoutines();
        }

        private static string GetRoutineKey(Type t)
        {
            const string suffix = "Routine";
            return t.Name.EndsWith(suffix, StringComparison.Ordinal)
                ? t.Name[..^suffix.Length]
                : t.Name;
        }

        private Dictionary<string, Type> DiscoverRoutines()
        {
            try
            {
                var groups = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(s => s.GetTypes())
                    .Where(t => !t.IsAbstract &&
                               !t.IsInterface &&
                               typeof(RoutineBase).IsAssignableFrom(t))
                    .GroupBy(GetRoutineKey)
                    .ToList();

                var result = new Dictionary<string, Type>();
                foreach (var group in groups)
                {
                    if (group.Count() > 1)
                    {
                        var typeNames = string.Join(", ", group.Select(t => t.FullName));
                        DebugWindow.LogError($"[CombatRoutineSelector] Skipping routine name collision '{group.Key}': {typeNames}");
                        continue;
                    }

                    result[group.Key] = group.First();
                }

                return result;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[CombatRoutineSelector] Error discovering routines: {ex.Message}");
                return new Dictionary<string, Type>();
            }
        }

        public List<string> GetAvailableRoutines()
        {
            return _routineTypes.Keys.ToList();
        }

        public RoutineBase GetRoutine()
        {
            try
            {
                var routineName = FollowHer.Instance.Settings.Combat.AvailableStrategies.Value;
                if (string.IsNullOrEmpty(routineName))
                {
                    DebugWindow.LogError("[CombatRoutineSelector] No routine selected");
                    return null;
                }

                if (!_routineTypes.TryGetValue(routineName, out var routineType))
                {
                    DebugWindow.LogError($"[CombatRoutineSelector] Could not find routine type: {routineName}");
                    return null;
                }

                var instance = (RoutineBase)Activator.CreateInstance(
                    routineType,
                    new object[] { _gameController }
                );

                return instance;
            }
            catch (MissingMethodException ex)
            {
                DebugWindow.LogError($"[CombatRoutineSelector] Missing constructor for routine: {ex.Message}");
                DebugWindow.LogError($"[CombatRoutineSelector] Make sure the routine has a constructor that takes (GameController)");
                return null;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[CombatRoutineSelector] Error creating routine: {ex.Message}");
                if (ex.InnerException != null)
                {
                    DebugWindow.LogError($"[CombatRoutineSelector] Inner exception: {ex.InnerException.Message}");
                }
                return null;
            }
        }
    }
}