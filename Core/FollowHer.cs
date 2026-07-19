using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using FollowHer.Core.Combat;
using FollowHer.Core.Combat.Rules;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using FollowHer.Features.Following;
using FollowHer.Features.Rendering;
using FollowHer.Settings;
using ImGuiNET;

namespace FollowHer
{
    public class FollowHer : BaseSettingsPlugin<FollowHerSettings>
    {
        public static FollowHer Instance;

        private IRoutine _activeRoutine;
        private readonly CombatRuleEditor _combatRuleEditor = new();
        private bool _isToggled;
        private bool _movementToggled;
        private bool _fightingToggled;
        private DateTime _nextResurrectClickAt = DateTime.MinValue;
        private DateTime _lastLeaderAttackDetectedAt = DateTime.MinValue;

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
                Input.RegisterKey(Settings.Hotkeys.PrecisionKey);
                Input.RegisterKey(Settings.Hotkeys.PrecisionToggleKey);
                Input.RegisterKey(Settings.Hotkeys.MovementToggleKey);
                Input.RegisterKey(Settings.Hotkeys.FightingToggleKey);

                Settings.Hotkeys.PrecisionKey.OnValueChanged += () => Input.RegisterKey(Settings.Hotkeys.PrecisionKey);
                Settings.Hotkeys.PrecisionToggleKey.OnValueChanged += () =>
                {
                    Input.RegisterKey(Settings.Hotkeys.PrecisionToggleKey);
                    _isToggled = false;
                };
                Settings.Hotkeys.MovementToggleKey.OnValueChanged += () =>
                {
                    Input.RegisterKey(Settings.Hotkeys.MovementToggleKey);
                    _movementToggled = false;
                };
                Settings.Hotkeys.FightingToggleKey.OnValueChanged += () =>
                {
                    Input.RegisterKey(Settings.Hotkeys.FightingToggleKey);
                    _fightingToggled = false;
                };

                // Movement > Enable is the persisted default state; the toggle key just flips a
                // runtime override seeded from it, rather than being OR'd against it forever
                // (which made the hotkey unable to ever turn movement off while Enable was on).
                _movementToggled = Settings.Movement.Enable.Value;
                Settings.Movement.Enable.OnValueChanged += (_, value) =>
                {
                    _movementToggled = value;
                };

                // Seed one rule profile per old hardcoded Routine on first run only, so switching
                // to the rule-based engine doesn't leave you with nothing to select - never
                // overwrites profiles you've since edited/added/deleted.
                if (Settings.Combat.Profiles.Count == 0)
                {
                    Settings.Combat.Profiles = DefaultRuleProfiles.CreateDefaults();
                }

                // One routine instance for the whole plugin lifetime now - "which build to play"
                // is just which named CombatRuleProfile is active (read fresh every tick by
                // RuleBasedRoutine), not a whole different hardcoded routine class to swap in.
                _activeRoutine = new RuleBasedRoutine(GameController);
                if (!_activeRoutine.Initialize())
                {
                    DebugWindow.LogError($"[{Name}] Failed to initialize combat routine");
                    return false;
                }

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
                if (GameController.Player?.IsAlive == false)
                {
                    TryResurrectAtCheckpoint();
                    return null;
                }

                if (Settings.Hotkeys.PrecisionToggleKey.PressedOnce())
                {
                    _isToggled = !_isToggled;
                }
                if (Settings.Hotkeys.MovementToggleKey.PressedOnce())
                {
                    _movementToggled = !_movementToggled;
                }
                if (Settings.Hotkeys.FightingToggleKey.PressedOnce())
                {
                    _fightingToggled = !_fightingToggled;
                }
                bool shouldAttack = false;
                if (Settings.Combat.AttackWhenLeaderIsAttacking)
                {
                    var leaderEntity = LeaderLocator.FindLeaderEntity(GameController, Settings.LeaderName.Value);

                    var actorComponent = leaderEntity?.GetComponent<Actor>();
                    if (actorComponent != null && leaderEntity.DistancePlayer < Settings.Combat.DistanceToLeaderToAttack)
                    {
                        var leaderAnimation = actorComponent.Animation;
                        var leaderIsAttackingNow = actorComponent.isAttacking &&
                                       // Movement skills can read as "attacking" via isAttacking, so every
                                       // movement-skill animation needs excluding here, not just Charge/LeapSlam:
                                       // the full Charge family covers Shield Charge, and Teleport is the shared
                                       // animation for Frostblink/FlameDash/LightningWarp/Blink Arrow.
                                       !(leaderAnimation == AnimationE.LeapSlam ||
                                         leaderAnimation == AnimationE.LeapSlamOffhand ||
                                         leaderAnimation == AnimationE.Charge ||
                                         leaderAnimation == AnimationE.ChargeStart ||
                                         leaderAnimation == AnimationE.ChargeEnd ||
                                         leaderAnimation == AnimationE.ChargeEndAlt ||
                                         leaderAnimation == AnimationE.ChargeEndLeft ||
                                         leaderAnimation == AnimationE.ChargeEndRight ||
                                         leaderAnimation == AnimationE.ChargeEnd180 ||
                                         leaderAnimation == AnimationE.Teleport);

                        if (leaderIsAttackingNow)
                        {
                            _lastLeaderAttackDetectedAt = DateTime.Now;
                        }

                        // Keep attacking for a short grace period after the leader was last seen
                        // attacking, rather than requiring isAttacking to read true on this exact
                        // tick - smooths over the windup/recovery gaps between individual swings.
                        var gracePeriodMs = Settings.Combat.AttackGracePeriod.Value;
                        shouldAttack = DateTime.Now - _lastLeaderAttackDetectedAt <= TimeSpan.FromMilliseconds(gracePeriodMs);
                    }


                }




                var combatActive = _isToggled || _fightingToggled || Input.GetKeyState(Settings.Hotkeys.PrecisionKey) || shouldAttack;
                var movementActive = Settings.Movement.Enable || _movementToggled;
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

        public override void DrawSettings()
        {
            base.DrawSettings();
            _combatRuleEditor.Draw(Settings.Combat);
        }

        // Always resurrect at the checkpoint rather than in town - independent of the
        // movement/fighting toggles, since a dead character can't do either anyway.
        private void TryResurrectAtCheckpoint()
        {
            if (DateTime.Now < _nextResurrectClickAt) return;

            var button = GameController.IngameState?.IngameUi?.ResurrectPanel?.ResurrectAtCheckpoint;
            if (button is not { IsVisible: true }) return;

            ClickElement(button);
            _nextResurrectClickAt = DateTime.Now.AddMilliseconds(1000);
        }

        private void ClickElement(Element element)
        {
            if (element == null) return;

            var position = element.GetClientRect().ClickRandomNum(5, 3) +
                            GameController.Window.GetWindowRectangle().TopLeft.ToVector2Num();

            Input.SetCursorPos(position);
            Input.Click(MouseButtons.Left);
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