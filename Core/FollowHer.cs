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
        private bool _movementToggled;
        private bool _fightingToggled;

        public bool MovementEnabled { get; private set; }
        public bool FightingEnabled { get; private set; }

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
                Input.RegisterKey(Settings.MovementToggleKey);
                Input.RegisterKey(Settings.FightingToggleKey);

                Settings.PrecisionKey.OnValueChanged += () => Input.RegisterKey(Settings.PrecisionKey);
                Settings.PrecisionToggleKey.OnValueChanged += () =>
                {
                    Input.RegisterKey(Settings.PrecisionToggleKey);
                    _isToggled = false;
                };
                Settings.MovementToggleKey.OnValueChanged += () =>
                {
                    Input.RegisterKey(Settings.MovementToggleKey);
                    _movementToggled = false;
                };
                Settings.FightingToggleKey.OnValueChanged += () =>
                {
                    Input.RegisterKey(Settings.FightingToggleKey);
                    _fightingToggled = false;
                };

                // Follow > Enable is the persisted default state; the toggle key just flips a
                // runtime override seeded from it, rather than being OR'd against it forever
                // (which made the hotkey unable to ever turn movement off while Enable was on).
                _movementToggled = Settings.Combat.Follow.Enable.Value;
                Settings.Combat.Follow.Enable.OnValueChanged += (_, value) =>
                {
                    _movementToggled = value;
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
            _fightingToggled = false;
            // Movement's toggle state is intentionally left alone - following the leader through
            // a zone transition is the whole point, so it must survive the transition itself.
            EventBus.Instance.Publish(new AreaChangeEvent { NewArea = area });
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
                if (Settings.MovementToggleKey.PressedOnce())
                {
                    _movementToggled = !_movementToggled;
                }
                if (Settings.FightingToggleKey.PressedOnce())
                {
                    _fightingToggled = !_fightingToggled;
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




                var combatActive = _isToggled || _fightingToggled || Input.GetKeyState(Settings.PrecisionKey) || shouldAttack;
                var movementActive = Settings.Combat.Follow.Enable || _movementToggled;
                var isActive = combatActive || movementActive;

                MovementEnabled = movementActive;
                FightingEnabled = combatActive;

                if (!isActive)
                {
                    _activeRoutine?.Stop();
                    return null;
                }

                if (isActive && _activeRoutine != null)
                {
                    EventBus.Instance.Publish(new TickEvent(true, combatActive));
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