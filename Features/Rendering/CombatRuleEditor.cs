using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.Shared.Helpers;
using FollowHer.Core.Combat.Rules;
using FollowHer.Settings;
using ImGuiNET;
using Newtonsoft.Json;

namespace FollowHer.Features.Rendering;

/// <summary>Custom ImGui editor for combat rule profiles - adapted from ReAgent's
/// Rule.Display/RuleGroup.DrawSettings pattern (same drag-reorder/delete-confirmation
/// primitives), scoped down to what a (SkillName, Condition) rule needs instead of ReAgent's
/// full action-type/side-effect system.</summary>
public class CombatRuleEditor
{
    private const string DragDropPayloadId = "FollowHerRuleIndex";
    private const string ProfileExportTag = "followher_profile_v1";

    private static readonly Vector4 OkColor = new(0, 255, 0, 255);
    private static readonly Vector4 IdleColor = new(255, 255, 0, 255);
    private static readonly Vector4 ErrorColor = new(255, 0, 0, 255);

    private string _newProfileName = "";
    private string _pendingDeleteProfile;
    private int _pendingDeleteRuleIndex = -1;
    private int _expandedRuleIndex = -1;
    private string _importText;

    public void Draw(CombatSettings settings)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Combat Rule Profiles");

        DrawProfileSelector(settings);

        if (!settings.Profiles.TryGetValue(settings.ActiveProfile, out var profile))
        {
            ImGui.TextUnformatted("No active profile - create or select one above.");
            return;
        }

        DrawRuleList(settings, profile);
    }

    private void DrawProfileSelector(CombatSettings settings)
    {
        var profileNames = settings.Profiles.Keys.OrderBy(n => n).ToList();

        var current = settings.ActiveProfile;
        if (ImguiExt.EnumerableComboBox("Active Profile", profileNames, ref current))
        {
            settings.ActiveProfile = current;
        }

        ImGui.InputText("New Profile Name", ref _newProfileName, 100);
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_newProfileName) || settings.Profiles.ContainsKey(_newProfileName));
        if (ImGui.Button("Add Profile"))
        {
            settings.Profiles[_newProfileName] = new CombatRuleProfile { Name = _newProfileName };
            settings.ActiveProfile = _newProfileName;
            _newProfileName = "";
        }
        ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(settings.ActiveProfile))
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete Active Profile"))
            {
                _pendingDeleteProfile = settings.ActiveProfile;
                ImGui.OpenPopup("ProfileDeleteConfirmation");
            }

            ImGui.SameLine();
            if (ImGui.Button("Export Active Profile"))
            {
                var activeProfile = settings.Profiles.GetValueOrDefault(settings.ActiveProfile);
                ImGui.SetClipboardText(DataExporter.ExportDataBase64(activeProfile, ProfileExportTag, new JsonSerializerSettings()));
            }
        }

        var deleteResult = ImguiExt.DrawDeleteConfirmationPopup("ProfileDeleteConfirmation",
            _pendingDeleteProfile == null ? null : $"profile '{_pendingDeleteProfile}'");
        if (deleteResult == true && _pendingDeleteProfile != null)
        {
            settings.Profiles.Remove(_pendingDeleteProfile);
            if (settings.ActiveProfile == _pendingDeleteProfile) settings.ActiveProfile = "";
        }

        if (deleteResult != null) _pendingDeleteProfile = null;

        _importText ??= "";
        ImGui.InputText("Import Text", ref _importText, 20000);
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_importText));
        if (ImGui.Button("Import Profile"))
        {
            try
            {
                var imported = DataExporter.ImportDataBase64(_importText, ProfileExportTag).ToObject<CombatRuleProfile>();
                if (imported != null)
                {
                    var name = string.IsNullOrWhiteSpace(imported.Name) ? "Imported profile" : imported.Name;
                    var uniqueName = name;
                    var suffix = 1;
                    while (settings.Profiles.ContainsKey(uniqueName)) uniqueName = $"{name} ({++suffix})";

                    imported.Name = uniqueName;
                    settings.Profiles[uniqueName] = imported;
                    settings.ActiveProfile = uniqueName;
                    _importText = "";
                }
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[CombatRuleEditor] Failed to import profile: {ex.Message}");
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawRuleList(CombatSettings settings, CombatRuleProfile profile)
    {
        var skillNames = settings.Skills.Content.Select(s => s.Name).ToList();

        for (var i = 0; i < profile.Rules.Count; i++)
        {
            ImGui.PushID($"CombatRule{i}");
            if (i != 0) ImGui.Separator();

            var dropTargetStart = ImGui.GetCursorPos();
            ImGui.PushStyleColor(ImGuiCol.Button, 0);
            ImGui.Button("=");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Drag to reorder - list order is priority");

            if (ImGui.BeginDragDropSource())
            {
                ImguiExt.SetDragDropPayload(DragDropPayloadId, i);
                ImGui.TextUnformatted($"Rule {i}: {profile.Rules[i].SkillName}");
                ImGui.EndDragDropSource();
            }

            if (ImGui.Button("Delete"))
            {
                if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                {
                    profile.Rules.RemoveAt(i);
                    ImGui.PopID();
                    break;
                }

                _pendingDeleteRuleIndex = i;
                ImGui.OpenPopup("CombatRuleDeleteConfirmation");
            }
            else if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Hold Shift to delete without confirmation");
            }

            ImGui.SameLine();
            DrawRule(profile.Rules[i], skillNames, _expandedRuleIndex == i);
            ImGui.SameLine();
            if (ImGui.SmallButton(_expandedRuleIndex == i ? "Collapse" : "Expand"))
            {
                _expandedRuleIndex = _expandedRuleIndex == i ? -1 : i;
            }

            ImguiExt.DrawLargeTransparentSelectable("##CombatRuleDragTarget", dropTargetStart);
            if (ImGui.BeginDragDropTarget())
            {
                var sourceIndex = ImguiExt.AcceptDragDropPayload<int>(DragDropPayloadId);
                if (sourceIndex != null && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    var moved = profile.Rules[sourceIndex.Value];
                    profile.Rules.RemoveAt(sourceIndex.Value);
                    profile.Rules.Insert(i, moved);
                }

                ImGui.EndDragDropTarget();
            }

            ImGui.PopID();
        }

        var ruleDeleteResult = ImguiExt.DrawDeleteConfirmationPopup("CombatRuleDeleteConfirmation",
            _pendingDeleteRuleIndex == -1 ? null : $"rule {_pendingDeleteRuleIndex}");
        if (ruleDeleteResult == true && _pendingDeleteRuleIndex >= 0 && _pendingDeleteRuleIndex < profile.Rules.Count)
        {
            profile.Rules.RemoveAt(_pendingDeleteRuleIndex);
        }

        if (ruleDeleteResult != null) _pendingDeleteRuleIndex = -1;

        if (ImGui.Button("Add New Rule"))
        {
            profile.Rules.Add(new CombatRule { SkillName = skillNames.FirstOrDefault() ?? "" });
        }
    }

    private void DrawRule(CombatRule rule, List<string> skillNames, bool expanded)
    {
        var enabled = rule.Enabled;
        if (ImGui.Checkbox("##Enabled", ref enabled)) rule.Enabled = enabled;
        ImGui.SameLine();

        if (expanded)
        {
            var skillName = rule.SkillName;
            if (ImguiExt.EnumerableComboBox("Skill", skillNames, ref skillName))
            {
                rule.SkillName = skillName;
            }

            var condition = rule.Condition;
            ImGui.TextWrapped("Condition");
            if (ImGui.InputTextMultiline("##Condition", ref condition, 10000,
                    new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 3)))
            {
                rule.Condition = condition;
                rule.ResetCompiledCondition();
            }
        }
        else
        {
            ImGui.TextUnformatted($"{rule.SkillName}: {Truncate(rule.Condition)}");
        }

        if (rule.LastError != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ErrorColor);
            ImGui.TextWrapped(rule.LastError);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextColored(rule.Enabled ? OkColor : IdleColor, rule.Enabled ? "Compiled OK" : "Disabled");
        }
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var singleLine = text.Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length > 60 ? singleLine[..60] + "..." : singleLine;
    }
}
