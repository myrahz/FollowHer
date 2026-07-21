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
        private DateTime _nextInviteClickAt = DateTime.MinValue;
        private DateTime _leaderInviteFirstSeenAt = DateTime.MinValue;
        private DateTime _lastLeaderAttackDetectedAt = DateTime.MinValue;

        public bool MovementEnabled { get; private set; }
        public bool FightingEnabled { get; private set; }
        // Fighting actually permitted this tick (master switch AND the combat gate, or a manual
        // override) - distinct from FightingEnabled, which is just the master switch state.
        public bool FightingActive { get; private set; }

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
                // Runs before the alive check - being dead is no reason to sit on a pending invite.
                TryAcceptLeaderInvite();

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
                // Fighting is a master switch (this hotkey) AND-ed with the combat conditions
                // below - enabling it never forces an attack on its own, it just permits one when
                // everything else agrees. This is the fix for "always attacking even if the leader
                // isn't": _fightingToggled used to be OR'd straight into combatActive, bypassing
                // every gate.
                var fightingEnabled = _fightingToggled;

                // The gate that decides whether attacking is *warranted* right now, independent of
                // whether fighting is enabled. When "Attack When Leader Is Attacking" is on, that
                // means the leader must actually be attacking and be within range of us. When off,
                // there's no leader-based restriction and the gate is always open (the routine's
                // own targeting range still applies downstream).
                var attackGateOpen = true;
                if (Settings.Combat.AttackWhenLeaderIsAttacking)
                {
                    attackGateOpen = false;
                    var leaderEntity = LeaderLocator.FindLeaderEntity(GameController, Settings.LeaderName.Value);

                    var actorComponent = leaderEntity?.GetComponent<Actor>();
                    if (actorComponent != null && leaderEntity.DistancePlayer < Settings.Combat.DistanceToLeaderToAttack)
                    {
                        // Every skill the leader uses counts as attacking unless it's blacklisted -
                        // see LeaderAttackDetector for why this is inverted from the old animation list.
                        var leaderIsAttackingNow = LeaderAttackDetector.IsAttacking(
                            actorComponent, Settings.Combat.LeaderSkillBlacklistEntries);

                        if (leaderIsAttackingNow)
                        {
                            _lastLeaderAttackDetectedAt = DateTime.Now;
                        }

                        // Keep attacking for a short grace period after the leader was last seen
                        // attacking, rather than requiring isAttacking to read true on this exact
                        // tick - smooths over the windup/recovery gaps between individual swings.
                        var gracePeriodMs = Settings.Combat.AttackGracePeriod.Value;
                        attackGateOpen = DateTime.Now - _lastLeaderAttackDetectedAt <= TimeSpan.FromMilliseconds(gracePeriodMs);
                    }
                }

                // Precision key is a manual override: hold PrecisionKey (or toggle it on) to force
                // fighting regardless of both the master switch and the leader gate.
                var forceAttack = _isToggled || Input.GetKeyState(Settings.Hotkeys.PrecisionKey);

                var combatActive = forceAttack || (fightingEnabled && attackGateOpen);

                // Movement follows a single runtime state: the hotkey flips _movementToggled, which
                // is seeded from (and kept in sync with) Movement > Enable. It must NOT be OR'd with
                // Enable here, or the hotkey could never turn following off while Enable is checked.
                var movementActive = _movementToggled;
                var isActive = combatActive || movementActive;

                MovementEnabled = movementActive;
                // Reflects the master fighting switch, so the overlay tells you whether your hotkey
                // took effect. Whether it's actually swinging this tick is FightingActive below.
                FightingEnabled = fightingEnabled;
                FightingActive = combatActive;

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
            _combatRuleEditor.DrawLeaderSkillBlacklist(Settings.Combat);
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

        // Accepts a pending party invite only when the inviter is the configured leader. Party
        // invites from anyone else, and every non-party invite kind (trade/friend/guild), are
        // deliberately left for the user to deal with.
        private void TryAcceptLeaderInvite()
        {
            if (!Settings.Party.AutoAcceptLeaderInvite) return;
            if (DateTime.Now < _nextInviteClickAt) return;

            var leaderName = Settings.LeaderName.Value;
            if (string.IsNullOrWhiteSpace(leaderName)) return;

            var invites = GameController.IngameState?.IngameUi?.InvitesPanel?.Invites;
            if (invites == null || invites.Count == 0)
            {
                _leaderInviteFirstSeenAt = DateTime.MinValue;
                return;
            }

            foreach (var invite in invites)
            {
                if (invite == null) continue;
                if (invite.Kind != InvitesPanelItemKind.Party) continue;
                if (!string.Equals(invite.Name?.Trim(), leaderName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                var button = invite.AcceptButton;
                if (button is not { IsVisible: true }) continue;

                // Hold off until the configured delay has elapsed since this invite first showed
                // up, rather than clicking on the frame it renders.
                if (_leaderInviteFirstSeenAt == DateTime.MinValue)
                {
                    _leaderInviteFirstSeenAt = DateTime.Now;
                    return;
                }

                if (DateTime.Now - _leaderInviteFirstSeenAt <
                    TimeSpan.FromMilliseconds(Settings.Party.AcceptDelay.Value)) return;

                ClickElement(button);
                _nextInviteClickAt = DateTime.Now.AddMilliseconds(1000);
                _leaderInviteFirstSeenAt = DateTime.MinValue;
                return;
            }

            _leaderInviteFirstSeenAt = DateTime.MinValue;
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