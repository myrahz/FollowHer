using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared.Nodes;
using FollowHer.Features.Following.Pathfinding;
using FollowHer.Features.Navigation;
using FollowHer.Settings;

namespace FollowHer.Features.Rendering;

/// <summary>The target navigator overlay: lists every Radar target in the current area with a button
/// that walks the character there using the follow pathfinding. Doubles as a test harness for that
/// pathfinding - pick something across the zone, watch whether A* actually gets there.</summary>
public class TargetNavigatorWindow
{
    // 1 grid unit = 250/23 world units - distances are shown in world units so they're comparable to
    // the distance settings elsewhere in the plugin.
    private const float GridToWorldMultiplier = 250f / 23f;

    private static readonly Vector4 OkColor = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 MutedColor = new(0.7f, 0.7f, 0.7f, 1f);

    private readonly RadarTargetSource _source = new();
    private string _filter = "";

    public void Invalidate() => _source.Invalidate();

    public void Draw(GameController gameController)
    {
        var settings = FollowHer.Instance.Settings.Navigation;
        if (!settings.ShowWindow) return;
        if (gameController?.Player == null) return;

        // Navigator mode only: in Follow mode there is nothing for this window to drive.
        if (FollowHer.Instance.CurrentMode != PluginMode.Navigator) return;

        var navigation = FollowHer.Instance.Navigation;

        ImGui.SetNextWindowSize(new Vector2(360, 420), ImGuiCond.FirstUseEver);

        var open = true;
        if (!ImGui.Begin("FollowHer - Target Navigator", ref open))
        {
            ImGui.End();
            return;
        }

        if (!open) settings.ShowWindow.Value = false;

        _source.EnsureScanned(gameController);

        DrawHeader(gameController, navigation);
        ImGui.Separator();
        DrawTargetList(gameController, navigation);

        ImGui.End();
    }

    // " (F3)" when the action has a key bound, nothing when it doesn't - so the buttons double as a
    // reminder of the bindings without turning into "Stop (None)".
    private static string HotkeySuffix(HotkeyNode key)
    {
        return key == null || key.Value == Keys.None ? "" : $" ({key.Value})";
    }

    private void DrawHeader(GameController gameController, NavigationManager navigation)
    {
        var settings = FollowHer.Instance.Settings.Navigation;

        if (ImGui.Button("Refresh")) _source.Scan(gameController);

        ImGui.SameLine();
        ImGui.BeginDisabled(navigation?.IsActive != true);
        if (ImGui.Button($"Stop{HotkeySuffix(settings.StopKey)}")) navigation?.Stop("stopped by user");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(navigation is not { CanResume: true, IsActive: false });
        if (ImGui.Button($"Walk to selected{HotkeySuffix(settings.MoveKey)}")) navigation?.Resume();
        ImGui.EndDisabled();

        ImGui.TextColored(_source.IsRadarConnected ? OkColor : WarnColor, _source.Status);

        if (navigation == null)
        {
            ImGui.TextColored(WarnColor, "Navigation is not initialised yet - enable the plugin and load into an area.");
            return;
        }

        var statusColor = navigation.IsActive ? OkColor : MutedColor;
        ImGui.TextColored(statusColor, $"Status: {navigation.Status}");

        // What the Move Key will walk to, and how it behaves - so the key's effect is never a guess.
        var selected = navigation.LastTarget?.Label ?? "none - click a target below";
        var behavior = settings.MoveKeyIsHold ? "hold" : "toggle";
        var moveKey = settings.MoveKey.Value == Keys.None ? "unbound" : settings.MoveKey.Value.ToString();
        ImGui.TextColored(MutedColor, $"Selected: {selected}   |   Move Key: {moveKey} ({behavior})");

        var fightKey = FollowHer.Instance.Settings.Hotkeys.PrecisionKey.Value;
        ImGui.TextColored(MutedColor,
            $"Fight: hold {(fightKey == Keys.None ? "Precision Key (unbound)" : fightKey.ToString())} - otherwise it moves");

        if (!FollowHer.Instance.Settings.Enable)
        {
            ImGui.TextColored(WarnColor, "FollowHer is disabled - navigation won't move the character.");
        }
    }

    private void DrawTargetList(GameController gameController, NavigationManager navigation)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##navfilter", "filter targets...", ref _filter, 100);

        var rows = BuildRows(gameController);
        if (rows.Count == 0)
        {
            ImGui.TextColored(MutedColor, _source.Targets.Count == 0
                ? "No Radar targets in this area."
                : "No target matches the filter.");
            return;
        }

        var activeLabel = navigation?.ActiveTarget?.Label;
        var selectedLabel = navigation?.LastTarget?.Label;

        if (ImGui.BeginChild("##navtargets", new Vector2(0, 0), ImGuiChildFlags.None))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var (target, distance) = rows[i];

                ImGui.PushID(i);
                if (ImGui.Button("Go")) navigation?.Start(target);
                ImGui.PopID();

                ImGui.SameLine();
                var isActive = activeLabel != null && activeLabel == target.Label;
                ImGui.TextColored(isActive ? OkColor : MutedColor, $"{distance,6:F0}");
                ImGui.SameLine();

                // Clicking the name selects it for the Move Key without starting a walk; Go selects
                // it and starts walking straight away.
                ImGui.PushID(i);
                if (ImGui.Selectable(target.Label, selectedLabel != null && selectedLabel == target.Label))
                {
                    navigation?.Select(target);
                }
                ImGui.PopID();

                if (ImGui.IsItemHovered())
                {
                    // The cell's state on our own walkable grid, so a target that won't path is
                    // diagnosable on the spot rather than only after pressing Go.
                    var cell = PathFollower.RoundGrid(target.GridPos);
                    var terrain = FollowHer.Instance.Navigation?.DescribeTargetCell(cell) ?? "unknown";
                    ImGui.SetTooltip($"{target.Pattern}\ngrid ({cell.x}, {cell.y}) - {terrain}");
                }
            }
        }

        ImGui.EndChild();
    }

    private List<(NavTarget Target, float Distance)> BuildRows(GameController gameController)
    {
        var playerGrid = gameController.Player.GridPosNum;

        return _source.Targets
            .Where(t => string.IsNullOrWhiteSpace(_filter) ||
                        t.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .Select(t => (Target: t, Distance: Vector2.Distance(playerGrid, t.GridPos) * GridToWorldMultiplier))
            .OrderBy(x => x.Distance)
            .ToList();
    }
}
