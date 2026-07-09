using ExileCore;
using FollowHer.Core.Combat;
using FollowHer.Core.Combat.Skills;
using FollowHer.Core.Combat.State;
using FollowHer.Features.Targeting.EntityInformation;
using FollowHer.Settings;
using FollowHer.Features.Targeting;
using FollowHer.Features.Targeting.Priority;
using FollowHer.Routines.LightningStrike.Strategy;
using FollowHer.Utils;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using System;
using System.Numerics;
using System.Collections.Generic;

namespace FollowHer.Routines.LightningStrike
{
    public class LightningStrikeRoutine : OrbWalkingRoutineBase
    {
        private readonly TargetSelector _targetSelector;
        private readonly SkillPriority _skillPriority;
        private readonly LineOfSight _lineOfSight;
        private readonly PriorityCalculator _priorityCalculator;

        public LightningStrikeRoutine(GameController gameController)
            : base("Lightning Strike", gameController)
        {
            _lineOfSight = new LineOfSight(gameController);

            var entityScanner = new EntityScanner(gameController, _lineOfSight);
            _priorityCalculator = new PriorityCalculator(gameController);

            _targetSelector = new TargetSelector(
                gameController,
                entityScanner,
                _priorityCalculator,
                _lineOfSight
            );

            _targetSelector.Configure();
            _skillPriority = new SkillPriority(gameController);

            var eventBus = EventBus.Instance;
            eventBus.Subscribe<RenderEvent>(HandleRender);
        }

        protected override void InitializeSkills()
        {
            try
            {
                SkillHandler.Initialize();
                StateCoordinator.SetState(RoutineState.Idle);
            }
            catch (Exception ex)
            {
                LogError($"Error initializing skills: {ex.Message}");
                StateCoordinator.SetError(ex);
            }
        }

        protected override (ActiveSkill skill, EntityInfo target) GetBestAction()
        {
            try
            {
                _targetSelector.Update(SkillHandler.GetAllSkills());
                return _skillPriority.GetBestAction(
                    SkillHandler.GetAllSkills(),
                    _targetSelector,
                    _priorityCalculator,
                    SkillMonitor
                );
            }
            catch (Exception ex)
            {
                LogError($"Error in GetBestAction: {ex.Message}");
                return (null, null);
            }
        }

        protected override void ExecuteCombatTick(ActiveSkill skill, EntityInfo target)
        {
            try
            {
                if (target == null || skill == null) return;

                var screenPos = target.ScreenPos;
                if (screenPos != Vector2.Zero)
                {
                    using (Input.InputManager.BlockUserMouseInput())
                    {
                        Input.InputManager.MoveMouse(screenPos);
                        //if (IsCursorOnTarget(target)) // doesnt really matter for Lightning Strike
                        {
                            SkillMonitor.TrackUse(skill);
                            SkillHandler.UseSkill(skill.Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error in ExecuteCombatTick: {ex.Message}");
            }
        }

        private void HandleRender(RenderEvent evt)
        {
            if (!FollowHer.Instance.Settings.Render.EnableRendering) return;

            try
            {
                CombatRenderer.Render(evt.Graphics, CurrentTarget, StateCoordinator.CurrentState);
            }
            catch (Exception ex)
            {
                LogError($"Error in render: {ex.Message}");
            }
        }

        protected override void HandleAreaChange(AreaChangeEvent evt)
        {
            _targetSelector?.Clear();
            StateCoordinator.Reset();
            base.HandleAreaChange(evt);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var eventBus = EventBus.Instance;
                eventBus.Unsubscribe<RenderEvent>(HandleRender);
                _targetSelector?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}