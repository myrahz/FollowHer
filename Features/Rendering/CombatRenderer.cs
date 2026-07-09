using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using FollowHer.Features.Targeting.EntityInformation;
using FollowHer.Settings;
using FollowHer.Core.Combat.State;
using Graphics = ExileCore.Graphics;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace FollowHer.Features.Rendering
{
    public interface ICombatRenderer
    {
        void Render(Graphics graphics, EntityInfo currentTarget, RoutineState state);
        void Clear();
    }

    public class CombatRenderer : ICombatRenderer
    {
        private readonly GameController _gameController;

        private List<Vector2> _movementPath = new();

        public CombatRenderer(GameController gameController)
        {
            _gameController = gameController;
        }

        public void Render(Graphics graphics, EntityInfo currentTarget, RoutineState state)
        {
            if (!FollowHer.Instance.Settings.Render.EnableRendering) return;
            if (_gameController?.Game?.IngameState?.IngameUi == null) return;

            var renderSettings = FollowHer.Instance.Settings.Render;

            if (currentTarget != null && currentTarget.IsValid && currentTarget.IsAlive)
            {
                if (renderSettings.TargetVisuals.ShowTargetHighlight)
                {
                    RenderTargetHighlight(graphics, currentTarget);
                }

                if (renderSettings.TargetVisuals.ShowTargetHealth)
                {
                    RenderTargetHealth(graphics, currentTarget);
                }
            }

            if (renderSettings.ShowDebugInfo)
            {
                RenderDebugInfo(graphics, currentTarget, state);
            }

        }

        private void RenderTargetHighlight(Graphics graphics, EntityInfo target)
        {
            var highlightSettings = FollowHer.Instance.Settings.Render.TargetVisuals;
            var bounds = _gameController.IngameState.Camera.WorldToScreen(target.Pos);

            if (bounds != Vector2.Zero)
            {
                var color = highlightSettings.TargetHighlightColor.Value;
                var thickness = highlightSettings.HighlightThickness.Value;
                var size = 25f;

                graphics.DrawCircle(bounds, size, color, thickness);

                float crosshairSize = size * 0.7f;
                graphics.DrawLine(
                    new Vector2(bounds.X - crosshairSize, bounds.Y),
                    new Vector2(bounds.X + crosshairSize, bounds.Y),
                    thickness,
                    color);
                graphics.DrawLine(
                    new Vector2(bounds.X, bounds.Y - crosshairSize),
                    new Vector2(bounds.X, bounds.Y + crosshairSize),
                    thickness,
                    color);
            }
        }

        private void RenderTargetHealth(Graphics graphics, EntityInfo target)
        {
            var healthSettings = FollowHer.Instance.Settings.Render.TargetVisuals;
            var position = _gameController.IngameState.Camera.WorldToScreen(target.Pos);

            if (position != Vector2.Zero)
            {
                var color = healthSettings.HealthTextColor.Value;
                var text = $"{(target.HPPercentage * 100):F2}%";
                var textPos = position + new Vector2(0, 30);

                graphics.DrawText(text, textPos, color);
            }
        }

        private void RenderDebugInfo(Graphics graphics, EntityInfo currentTarget, RoutineState state)
        {
            var debugInfo = new List<string>
            {
                $"State: {state}",
                $"Cursor Pos: {ExileCore.Input.MousePositionNum}"
            };

            if (currentTarget != null && currentTarget.IsValid && currentTarget.IsAlive)
            {
                debugInfo.Add($"Target: {currentTarget.Path}");
                debugInfo.Add($"Distance: {currentTarget.Entity.DistancePlayer:F1}");
                debugInfo.Add($"Health: {(currentTarget.HPPercentage * 100):F2}%");
                debugInfo.Add($"ES: {(currentTarget.ESPercentage * 100):F2}%");
                debugInfo.Add($"Rarity: {currentTarget.Rarity}");
            }

            var startPos = new Vector2(10, 200);
            var color = Color.White;
            var lineHeight = 20f;

            for (int i = 0; i < debugInfo.Count; i++)
            {
                graphics.DrawText(
                    debugInfo[i],
                    startPos + new Vector2(0, i * lineHeight),
                    color);
            }
        }

        public void Clear()
        {
            _movementPath.Clear();
        }
    }
}