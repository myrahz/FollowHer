using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FollowHer.Core.Combat.Rules;
using FollowHer.Core.Combat.Skills;
using FollowHer.Core.Combat.State;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using FollowHer.Features.Targeting;
using FollowHer.Features.Targeting.EntityInformation;
using FollowHer.Features.Targeting.Priority;
using FollowHer.Utils;

namespace FollowHer.Core.Combat;

/// <summary>The single, generic combat routine - replaces every hand-written per-strategy
/// Routine class. What used to require a new C# file (a wrapper .cs + a Strategy/SkillPriority.cs)
/// is now just a CombatRuleProfile the user builds in the settings UI: an ordered list of
/// (SkillName, Condition) rules, evaluated top-to-bottom against the (already generic, reused
/// as-is) targeting pipeline.</summary>
public class RuleBasedRoutine : OrbWalkingRoutineBase
{
    private readonly TargetSelector _targetSelector;
    private readonly LineOfSight _lineOfSight;
    private readonly PriorityCalculator _priorityCalculator;

    public RuleBasedRoutine(GameController gameController)
        : base("RuleBased", gameController)
    {
        _lineOfSight = new LineOfSight(gameController);

        var entityScanner = new EntityScanner(gameController, _lineOfSight);
        _priorityCalculator = new PriorityCalculator(gameController);

        _targetSelector = new TargetSelector(gameController, entityScanner, _priorityCalculator, _lineOfSight);
        _targetSelector.Configure();

        EventBus.Instance.Subscribe<RenderEvent>(HandleRender);
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

            var profile = GetActiveProfile();
            if (profile == null) return (null, null);

            foreach (var rule in profile.Rules)
            {
                if (!rule.Enabled) continue;

                var skill = SkillHandler.GetSkill(rule.SkillName);
                if (skill == null || !skill.Enabled || !SkillMonitor.CanUseSkill(skill)) continue;

                Entity bestCandidate = null;
                var bestWeight = float.MinValue;

                foreach (var candidate in _targetSelector.GetValidTargets(skill))
                {
                    var context = new SkillRuleContext(GameController, candidate);
                    if (!rule.Evaluate(context)) continue;

                    var weight = _priorityCalculator.GetEntityWeight(candidate) ?? float.MinValue;
                    if (weight > bestWeight)
                    {
                        bestWeight = weight;
                        bestCandidate = candidate;
                    }
                }

                // First rule (in priority order) with any matching candidate wins outright -
                // makes rule order a real, deterministic priority rather than a global
                // highest-weight-wins across every skill regardless of its rank.
                if (bestCandidate != null)
                {
                    return (skill, new EntityInfo(bestCandidate, GameController));
                }
            }

            return (null, null);
        }
        catch (Exception ex)
        {
            LogError($"Error in GetBestAction: {ex.Message}");
            return (null, null);
        }
    }

    private CombatRuleProfile GetActiveProfile()
    {
        var settings = FollowHer.Instance.Settings.Combat;
        return settings.Profiles.GetValueOrDefault(settings.ActiveProfile);
    }

    protected override void ExecuteCombatTick(ActiveSkill skill, EntityInfo target)
    {
        try
        {
            if (skill == null || target == null) return;

            if (!skill.RequiresAiming)
            {
                SkillMonitor.TrackUse(skill);
                SkillHandler.UseSkill(skill.Name);
                return;
            }

            var screenPos = target.ScreenPos;
            if (screenPos == Vector2.Zero) return;

            using (Input.InputManager.BlockUserMouseInput())
            {
                Input.InputManager.MoveMouse(screenPos);
                if (IsCursorOnTarget(target))
                {
                    SkillMonitor.TrackUse(skill);
                    SkillHandler.UseSkill(skill.Name);
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
            EventBus.Instance.Unsubscribe<RenderEvent>(HandleRender);
            _targetSelector?.Dispose();
        }
        base.Dispose(disposing);
    }
}
