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
        private const string MOVE_SKILL_NAME = "Move";
        private Vector2 _preTargetMousePosition;
        private bool _inCombat;

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
                }
                else
                {
                    if (_inCombat) EndCombat();
                    StateCoordinator.SetState(RoutineState.Orbwalking);
                    Orbwalk();
                }
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

            if (FollowHer.Instance.Settings.Combat.MovementSkills.Content.Where(x => x.Enabled).Any())
            {
                var movementSkill = FollowHer.Instance.Settings.Combat.MovementSkills.Content
                    .FirstOrDefault(x => x.Enabled && SkillMonitor.CanUseSkill(x));

                if (movementSkill != null)
                {
                    SkillHandler.UseMovementSkill(movementSkill.Name, true);
                    return;
                }
            }

            SkillHandler.UseMovementSkill(MOVE_SKILL_NAME, true);
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
            }
            base.Dispose(disposing);
        }
    }
}