using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using FollowHer.Core.Combat;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using FollowHer.Features.Following;
using FollowHer.Settings;
using ImGuiNET;

namespace FollowHer
{
    public class FollowHer : BaseSettingsPlugin<FollowHerSettings>
    {
        public static FollowHer Instance;

        private IRoutine _activeRoutine;
        private bool _isToggled;

        public FollowHer()
        {
            Name = "FollowHer";
            Instance = this;
        }

        public override bool Initialise()
        {
            try
            {
                Input.RegisterKey(Settings.PrecisionKey);
                Input.RegisterKey(Settings.PrecisionToggleKey);

                Settings.PrecisionKey.OnValueChanged += () => Input.RegisterKey(Settings.PrecisionKey);
                Settings.PrecisionToggleKey.OnValueChanged += () =>
                {
                    Input.RegisterKey(Settings.PrecisionToggleKey);
                    _isToggled = false;
                };

                var routineSelector = new CombatRoutineSelector(GameController);

                var availableRoutines = routineSelector.GetAvailableRoutines();
                Settings.Combat.AvailableStrategies.SetListValues(availableRoutines);

                if (Settings.Combat.AvailableStrategies.Values.Count == 0)
                {
                    DebugWindow.LogError($"[{Name}] No combat routines available");
                    return false;
                }

                try
                {
                    if (!string.IsNullOrEmpty(Settings.Combat.AvailableStrategies.Value))
                    {
                        _activeRoutine = routineSelector.GetRoutine();
                        if (_activeRoutine == null)
                        {
                            DebugWindow.LogError($"[{Name}] Failed to create combat routine");
                            return false;
                        }
                        if (!_activeRoutine.Initialize())
                        {
                            DebugWindow.LogError($"[{Name}] Failed to initialize combat routine");
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugWindow.LogError($"[{Name}] Failed to initialize combat routine: {ex.Message}");
                    return false;
                }

                Settings.Combat.AvailableStrategies.OnValueSelected += (strategy) =>
                {
                    DebugWindow.LogMsg($"[{Name}] Selected strategy: {strategy}");
                    _activeRoutine?.Dispose();
                    _activeRoutine = routineSelector.GetRoutine();
                    _activeRoutine?.Initialize();
                };

                return true;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[{Name}] Failed to initialize: {ex.Message}");
                return false;
            }
        }

        public override void AreaChange(AreaInstance area)
        {
            _isToggled = false;
            EventBus.Instance.Publish(new AreaChangeEvent { NewArea = area });
        }

        public override void EntityAdded(Entity entity)
        {
            if (entity != null && entity.IsValid)
            {
                EventBus.Instance.Publish(new EntityDiscoveredEvent
                {
                    Entity = entity,
                    Distance = entity.DistancePlayer
                });
            }
        }

        public override Job Tick()
        {
            if (!ShouldProcess()) return null;

            try
            {
                if (Settings.PrecisionToggleKey.PressedOnce())
                {
                    _isToggled = !_isToggled;
                }
                bool shouldAttack = false;
                if (Settings.AttackWhenLeaderIsAttacking)
                {
                    var leaderEntity = LeaderLocator.FindLeaderEntity(GameController, Settings.LeaderName.Value);

                    var actorComponent = leaderEntity?.GetComponent<Actor>();
                    if (actorComponent != null && leaderEntity.DistancePlayer < Settings.DistanceToLeaderToAttack)
                    {
                        var leaderAnimation = actorComponent.Animation;
                        var leaderIsAttacking = actorComponent.isAttacking;
                        shouldAttack = leaderIsAttacking &&
                                       !(leaderAnimation == AnimationE.LeapSlam ||
                                         leaderAnimation == AnimationE.LeapSlamOffhand ||
                                         leaderAnimation == AnimationE.Charge ||
                                         leaderAnimation == AnimationE.ChargeEnd);
                    }


                }




                var isActive = _isToggled || Input.GetKeyState(Settings.PrecisionKey) || shouldAttack;
                if (!isActive)
                {
                    _activeRoutine?.Stop();
                    return null;
                }

                if (isActive && _activeRoutine != null)
                {
                    EventBus.Instance.Publish(new TickEvent(true));
                }
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[{Name}] Error in Tick: {ex.Message}");
            }

            return null;
        }

        public override void Render()
        {
            if (!Settings.Render.EnableRendering) return;

            EventBus.Instance.Publish(new RenderEvent(Graphics));
        }

        private bool ShouldProcess()
        {
            if (!Settings.Enable) return false;
            if (GameController?.InGame != true) return false;
            if (GameController.Player == null) return false;
            if (GameController.Settings.CoreSettings.Enable) return false;

            return ValidateUIState();
        }

        private bool ValidateUIState()
        {
            var ingameUI = GameController?.IngameState?.IngameUi;
            if (ingameUI == null) return false;

            if (!Settings.Render.Interface.EnableWithFullscreenUI &&
                ingameUI.FullscreenPanels.Any(x => x.IsVisible))
                return false;

            if (!Settings.Render.Interface.EnableWithLeftPanel &&
                ingameUI.OpenLeftPanel.IsVisible)
                return false;

            if (!Settings.Render.Interface.EnableWithRightPanel &&
                ingameUI.OpenRightPanel.IsVisible)
                return false;

            return true;
        }
    }
}