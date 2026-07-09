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

        private Dictionary<string, Type> DiscoverRoutines()
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(s => s.GetTypes())
                    .Where(t => !t.IsAbstract &&
                               !t.IsInterface &&
                               typeof(RoutineBase).IsAssignableFrom(t))
                    .ToDictionary(
                        t => t.Name.Replace("Routine", ""),
                        t => t
                    );
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