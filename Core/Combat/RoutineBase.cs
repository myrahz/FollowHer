using FollowHer.Core.Combat.Skills;
using FollowHer.Core.Combat.State;
using FollowHer.Features.Targeting.EntityInformation;
using FollowHer.Features.Input;
using ExileCore;
using System;
using FollowHer.Settings;
using System.Numerics;
using FollowHer.Features.Rendering;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using ExileCore.PoEMemory.MemoryObjects;
using System.Collections.Generic;

namespace FollowHer.Core.Combat
{
    public abstract class RoutineBase : IRoutine
    {
        protected readonly GameController GameController;
        protected readonly SkillMonitor SkillMonitor;
        protected readonly SkillHandler SkillHandler;
        protected readonly KeyHandler KeyHandler;
        protected readonly StateCoordinator StateCoordinator;
        protected readonly ICombatRenderer CombatRenderer;

        protected EntityInfo CurrentTarget;
        protected bool IsInitialized;
        protected bool IsDisposed;

        public string Name { get; }
        public bool CanExecute => ValidateExecutionState();
        public RoutineState State => StateCoordinator.CurrentState;

        protected RoutineBase(string name, GameController gameController)
        {
            Name = name;
            GameController = gameController;

            SkillMonitor = new SkillMonitor();
            SkillHandler = new SkillHandler(gameController);
            KeyHandler = new KeyHandler();
            StateCoordinator = new StateCoordinator();
            CombatRenderer = new CombatRenderer(gameController);

            SubscribeToEvents();
        }

        private void SubscribeToEvents()
        {
            var eventBus = EventBus.Instance;
            eventBus.Subscribe<AreaChangeEvent>(HandleAreaChange);
            eventBus.Subscribe<TickEvent>(HandleTick);
            eventBus.Subscribe<TargetChangedEvent>(HandleTargetChanged);
        }

        public virtual bool Initialize()
        {
            if (IsDisposed)
                return false;

            try
            {
                if (!ValidateGameState())
                    return false;

                InitializeSkills();
                IsInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize routine: {ex.Message}");
                return false;
            }
        }

        protected virtual void HandleAreaChange(AreaChangeEvent evt)
        {
            if (IsDisposed) return;

            try
            {
                Stop();
                InitializeSkills();
                StateCoordinator.Reset();
            }
            catch (Exception ex)
            {
                LogError($"Error handling area change: {ex.Message}");
            }
        }

        protected virtual void HandleTick(TickEvent evt)
        {
            if (!CanExecute || IsDisposed) return;

            try
            {
                if (evt.IsActive)
                {
                    StateCoordinator.SetState(RoutineState.Active);
                    OnTickActive();
                }
                else
                {
                    StateCoordinator.SetState(RoutineState.Idle);
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

        protected virtual void OnTickActive() { }

        protected virtual void HandleTargetChanged(TargetChangedEvent evt)
        {
            if (IsDisposed) return;

            try
            {
                CurrentTarget = evt.NewTarget;
                OnTargetChanged(evt.OldTarget, evt.NewTarget);
            }
            catch (Exception ex)
            {
                LogError($"Error handling target change: {ex.Message}");
                Stop();
            }
        }

        protected virtual void OnCombatStart() { }
        protected virtual void OnCombatEnd() { }
        protected virtual void OnTargetChanged(EntityInfo oldTarget, EntityInfo newTarget) { }
        protected virtual void InitializeSkills() { }

        // Fixed screen-space aiming tolerance - the removed CombatRange setting was carried by
        // every routine but every one of them treated its actual value as inconsequential.
        private const float CursorOnTargetTolerance = 50f;

        protected bool IsCursorOnTarget(EntityInfo target)
        {
            var cursorPos = new Vector2(GameController.IngameState.MousePosX, GameController.IngameState.MousePosY);
            var targetPos = GameController.IngameState.Camera.WorldToScreen(target.Pos);

            return Vector2.Distance(cursorPos, targetPos) <= CursorOnTargetTolerance;
        }

        protected bool ValidateExecutionState()
        {
            if (!IsInitialized || IsDisposed)
                return false;

            return ValidateGameState();
        }

        protected virtual bool ValidateGameState()
        {
            return GameController != null &&
                   GameController.Game.IngameState?.Data != null &&
                   GameController.Game.IngameState.IngameUi != null &&
                   GameController.Player != null;
        }

        protected virtual bool ValidateTarget()
        {
            return CurrentTarget != null &&
                   CurrentTarget.IsValid &&
                   CurrentTarget.IsAlive &&
                   !CurrentTarget.IsHidden;
        }

        protected void LogError(string message)
        {
            DebugWindow.LogError($"[{Name}] {message}");
        }

        public virtual void Stop()
        {
            if (IsDisposed) return;

            try
            {
                KeyHandler.ReleaseAll();
                StateCoordinator.Reset();
                CurrentTarget = null;
            }
            catch (Exception ex)
            {
                LogError($"Error stopping routine: {ex.Message}");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                {
                    try
                    {
                        Stop();
                        KeyHandler.Dispose();
                        SkillMonitor.Reset();
                        CombatRenderer.Clear();

                        var eventBus = EventBus.Instance;
                        eventBus.Unsubscribe<AreaChangeEvent>(HandleAreaChange);
                        eventBus.Unsubscribe<TickEvent>(HandleTick);
                        eventBus.Unsubscribe<TargetChangedEvent>(HandleTargetChanged);
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error disposing routine: {ex.Message}");
                    }
                }
                IsDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~RoutineBase()
        {
            Dispose(false);
        }
    }
}