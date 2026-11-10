using ExileCore;
using FollowHer.Core.Combat.Skills;
using FollowHer.Core.Combat.State;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using FollowHer.Features.Following;
using FollowHer.Features.Targeting.EntityInformation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FollowHer.Core.Combat
{
    public abstract class OrbWalkingRoutineBase : RoutineBase
    {
        private Vector2 _preTargetMousePosition;
        private bool _inCombat;
        private bool _combatAllowed = true;

        protected readonly FollowManager FollowManager;

        protected OrbWalkingRoutineBase(string name, GameController gameController)
            : base(name, gameController)
        {
            FollowManager = new FollowManager(gameController, SkillHandler, SkillMonitor);
        }

        protected override void HandleAreaChange(AreaChangeEvent evt)
        {
            FollowManager.Reset(evt.NewArea);
            base.HandleAreaChange(evt);
        }

        protected override void HandleTick(TickEvent evt)
        {
            if (!CanExecute || IsDisposed) return;

            try
            {
                if (evt.IsActive)
                {
                    _combatAllowed = evt.CombatAllowed;
                    OnTickActive();
                }
                else
                {
                    Stop();
                }
            }
            catch (Exception ex)
            {
                LogError($"Error handling tick: {ex.Message}");
                Stop();
                StateCoordinator.SetError(ex);
            }
        }

        protected override void OnTickActive()
        {
            if (!CanExecute)
            {
                Stop();
                return;
            }

            try
            {
                // Pick up skills moved or rebound since the last read (throttled to once a second).
                SkillHandler.RefreshIfSkillBarChanged();

                if (_combatAllowed)
                {
                    var (skill, target) = GetBestAction();

                    if (target?.Entity?.Address != CurrentTarget?.Entity?.Address)
                    {
                        EventBus.Instance.Publish(new TargetChangedEvent
                        {
                            OldTarget = CurrentTarget,
                            NewTarget = target
                        });

                        CurrentTarget = target;
                        SkillHandler.ReleaseAllSkills();
                    }

                    if (CurrentTarget != null && skill != null)
                    {
                        if (!_inCombat) BeginCombat();
                        StateCoordinator.SetState(RoutineState.Active);
                        ExecuteCombatTick(skill, CurrentTarget);
                        return;
                    }
                }
                else if (CurrentTarget != null)
                {
                    // Combat isn't allowed this tick (Follow-only activation) - drop any stale target.
                    CurrentTarget = null;
                    SkillHandler.ReleaseAllSkills();
                }

                if (_inCombat) EndCombat();
                StateCoordinator.SetState(RoutineState.Orbwalking);
                Orbwalk();
            }
            catch (Exception ex)
            {
                LogError($"Error in OnTickActive: {ex.Message}");
                Stop();
                StateCoordinator.SetError(ex);
            }
        }

        protected abstract (ActiveSkill skill, EntityInfo target) GetBestAction();
        protected abstract void ExecuteCombatTick(ActiveSkill skill, EntityInfo target);

        protected void BeginCombat()
        {
            if (!_inCombat)
            {
                _preTargetMousePosition = ExileCore.Input.MousePositionNum;
                _inCombat = true;
                SkillHandler.ReleaseAllSkills();
            }
        }

        protected void EndCombat()
        {
            if (_inCombat)
            {
                ExileCore.Input.SetCursorPos(_preTargetMousePosition);
                _inCombat = false;
                SkillHandler.ReleaseAllSkills();
            }
        }

        protected void Orbwalk()
        {
            if (_inCombat) return;
            if (FollowManager.Update()) return;

            // FollowManager returning false means it has no destination at all this tick - follow is
            // off, no leader is configured, the character isn't alive yet, or its update threw. There
            // is nothing sensible to move *toward* in any of those cases, so release the movement key
            // rather than press it.
            //
            // It used to hold Move here instead. With no destination the game moves the character at
            // whatever the cursor happens to be over, and holding it kept them going: that is the
            // "plugin presses the movement key on zone change" bug. A zone load hits this path for a
            // frame or two (the player entity reads not-alive while it initialises), and holding Move
            // across those frames walks the character off the entrance.
            SkillHandler.ReleaseAllSkills();
        }

        public override void Stop()
        {
            if (_inCombat)
            {
                EndCombat();
            }
            SkillHandler.ReleaseAllSkills();
            FollowManager.Stop();
            base.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
                FollowManager.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}