using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Nodes;
using FollowHer.Features.Input;
using FollowHer.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FollowHer.Core.Combat.Skills
{
    public class SkillHandler
    {
        private readonly GameController _gameController;
        private readonly KeyHandler _keyHandler;
        private readonly Dictionary<string, ActiveSkill> _skills = new();
        private readonly Dictionary<string, MovementSkill> _movementSkills = new();

        public SkillHandler(GameController gameController)
        {
            _gameController = gameController;
            _keyHandler = new KeyHandler();
        }

        // Normalized (lowercase, whitespace-stripped) so a game name of "Frostblink" matches
        // regardless of the exact casing/spacing the game reports - the old exact-case string[]
        // meant "FrostBlink" never matched "Frostblink" and blink was silently never captured.
        private static readonly HashSet<string> MovementSkillNames = new()
        {
            "move", "dash", "flamedash", "frostblink", "lightningwarp",
            "shieldcharge", "newshieldcharge", "leapslam", "whirlingblades", "blinkarrow"
        };

        private static string NormalizeSkillName(string s) =>
            string.IsNullOrEmpty(s) ? "" : new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

        // Matches against both the display name and the internal name, so a movement skill is
        // recognized whichever field the game populates.
        private static bool IsMovementSkill(ActorSkill skill) =>
            MovementSkillNames.Contains(NormalizeSkillName(skill.Name)) ||
            MovementSkillNames.Contains(NormalizeSkillName(skill.InternalName));

        // Skills that teleport through obstacles rather than colliding with them - seeds the
        // default for TravelsThroughObstacles when a movement skill is first discovered. Normalized
        // like MovementSkillNames so casing/spacing differences don't misclassify a blink as a
        // ground dash.
        private static readonly HashSet<string> BlinkTypeMovementSkills =
            new() { "frostblink", "flamedash", "lightningwarp", "blinkarrow" };

        private static bool IsBlinkTypeMovementSkill(ActorSkill skill) =>
            BlinkTypeMovementSkills.Contains(NormalizeSkillName(skill.Name)) ||
            BlinkTypeMovementSkills.Contains(NormalizeSkillName(skill.InternalName));

        // Skills that carry the character over gaps and low obstacles, with a starting estimate of
        // their range in world units. Except where noted these are guesses, not read from the game -
        // the real range moves with gems and gear - so they err low (a shortcut missed is harmless, a
        // jump that falls short wastes a few seconds) and are meant to be corrected in the skill's
        // settings.
        //
        // "Since" is the defaults version that introduced the entry: a skill already seeded at an
        // older version still picks up entries added after it, but is never re-seeded for one it has
        // already had (that would overwrite a range the user set, or zeroed on purpose).
        //
        // "Replaces" is an earlier estimate that has since been corrected. A skill still sitting
        // exactly on it was never touched by the user, so it moves to the new value; anything else is
        // left alone.
        private const int JumpDefaultsCurrentVersion = 3;

        private static readonly Dictionary<string, (int range, int since, int replaces)> JumpSkillDefaults = new()
        {
            ["flamedash"] = (400, 1, 0),
            ["frostblink"] = (500, 1, 0),

            // Measured in game: one max-range cast moved the character 660 world units. 400 was the
            // original guess.
            ["leapslam"] = (660, 1, 400),

            ["blinkarrow"] = (500, 2, 0),
            ["lightningwarp"] = (450, 2, 0),
        };

        private static void ApplyJumpDefaults(MovementSkill activeSkill, ActorSkill skill)
        {
            // Settings saved while this was a one-time flag were seeded with the version-1 entries.
            if (activeSkill.JumpDefaultsVersion < 1 && activeSkill.JumpDefaultsApplied)
            {
                activeSkill.JumpDefaultsVersion = 1;
            }

            var seededVersion = activeSkill.JumpDefaultsVersion;
            if (seededVersion >= JumpDefaultsCurrentVersion) return;
            activeSkill.JumpDefaultsVersion = JumpDefaultsCurrentVersion;

            if (!JumpSkillDefaults.TryGetValue(NormalizeSkillName(skill.Name), out var entry) &&
                !JumpSkillDefaults.TryGetValue(NormalizeSkillName(skill.InternalName), out entry))
            {
                return;
            }

            if (entry.since > seededVersion ||
                (entry.replaces > 0 && activeSkill.MaxJumpDistance.Value == entry.replaces))
            {
                activeSkill.MaxJumpDistance.Value = entry.range;
            }
        }

        // Per-skill values for settings that used to be global, for skills we know something about. A
        // skill not listed here is seeded from the old global value instead (or a neutral default).
        // Min distance: the shortest use that is still worth it (Shield Charge measured at about 260
        // units; Leap Slam only beats walking above roughly 260-330). Hop: how far one use carries.
        // Clearance: the radius (cells) around the character that must be clear before it fires.
        private static readonly Dictionary<string, (int min, int hop, float clearance)> MovementSkillDefaults = new()
        {
            ["shieldcharge"] = (260, 800, 4f),
            ["newshieldcharge"] = (260, 800, 4f),
            ["leapslam"] = (326, 660, 0.5f),
        };

        /// <summary>One-time move of the settings that used to be global (Dash Enabled, Prefer Movement
        /// Skills, Min Distance, Clearance Margin, Dash Shortcut Ratio, Jump Path Cost) onto each skill.
        /// A value the skill already carries is never overwritten, so running this on an already-set-up
        /// skill only fills the gaps.</summary>
        private static void ApplyMovementSettingsMigration(MovementSkill movementSkill, ActorSkill skill)
        {
            var name = NormalizeSkillName(skill.Name);

            // Version 2: Frostblink always travels its full distance, whatever is clicked (measured).
            if (movementSkill.MovementSettingsVersion < 2)
            {
                if (name == "frostblink") movementSkill.TravelsFullDistance.Value = true;
            }

            if (movementSkill.MovementSettingsVersion >= 1)
            {
                movementSkill.MovementSettingsVersion = 2;
                return;
            }

            movementSkill.MovementSettingsVersion = 2;
            if (name == "move") return;

            var legacy = FollowHer.Instance.Settings.Movement;
            var known = MovementSkillDefaults.TryGetValue(name, out var entry) ||
                        MovementSkillDefaults.TryGetValue(NormalizeSkillName(skill.InternalName), out entry);
            var blink = movementSkill.TravelsThroughObstacles.Value;

            // Roles: off unless the old global switch was on - a fresh install keeps doing nothing
            // fancy until a skill is switched on.
            movementSkill.UseToCrossBlockedPaths.Value = blink && (legacy.LegacyDashEnabled?.Value ?? false);
            movementSkill.UseForTravel.Value = !blink && (legacy.LegacyPreferMovementSkillsForTravel?.Value ?? false);

            if (movementSkill.MinDistance.Value == 0)
                movementSkill.MinDistance.Value = known ? entry.min : legacy.LegacyMinDistance?.Value ?? 100;

            if (movementSkill.HopDistance.Value == 0 && known)
                movementSkill.HopDistance.Value = entry.hop;

            if (movementSkill.ClearanceRadius.Value == 0f)
                movementSkill.ClearanceRadius.Value = known ? entry.clearance : legacy.LegacyClearanceMargin?.Value ?? 0.5f;

            movementSkill.ShortcutPathRatio.Value = legacy.LegacyDashShortcutPathRatio?.Value ?? 1.5f;
            movementSkill.JumpPathCost.Value = legacy.LegacyJumpPathCost?.Value ?? 20;
        }

        // The key a skill is pressed with has to follow the game's own binding. A skill that already
        // existed in the saved settings used to keep whatever key it was first discovered with, so one
        // bad reading was stored and then reused on every later start - even after the cause was fixed.
        private static void ArchiveOffBar<T>(Dictionary<string, T> archive,
            Dictionary<string, T> known, Dictionary<string, T> onBar) where T : SkillBase
        {
            archive.Clear();
            foreach (var (name, skill) in known)
            {
                if (!onBar.ContainsKey(name)) archive[name] = skill;
            }
        }

        private static void SyncKey(SkillBase activeSkill, System.Windows.Forms.Keys key)
        {
            if (activeSkill.Key.Value != key) activeSkill.Key.Value = key;
        }

        private string _barSignature;
        private DateTime _nextBarCheckAt = DateTime.MinValue;
        private const int BarCheckIntervalMs = 1000;

        /// <summary>What's on the skill bar and the key each slot is bound to, as one string - changes
        /// when you move a skill, swap one in, or rebind a slot.</summary>
        private string BuildSkillBarSignature()
        {
            var skillBar = _gameController.IngameState.IngameUi.SkillBar;
            var shortcuts = _gameController.IngameState.ShortcutSettings.Shortcuts;
            var parts = new List<string>();

            for (var i = 0; i < Math.Min(13, skillBar.Skills.Count); i++)
            {
                var skill = skillBar.Skills[i]?.Skill;
                var name = skill == null || skill.Address == 0 ? "-" : $"{skill.Name}:{skill.Id}";
                var key = shortcuts.FirstOrDefault(x => x.Usage.ToString() == $"Skill{i + 1}").MainKey.ToString();
                parts.Add($"{name}@{key}");
            }

            return string.Join("|", parts);
        }

        /// <summary>Skills are read when the plugin starts and on every area change, so moving a skill or
        /// rebinding its key mid-zone went unnoticed until the next zone. This is a cheap check, once a
        /// second, that re-reads the bar when its contents or bindings differ from the last read.</summary>
        public void RefreshIfSkillBarChanged()
        {
            if (DateTime.Now < _nextBarCheckAt) return;
            _nextBarCheckAt = DateTime.Now.AddMilliseconds(BarCheckIntervalMs);

            string signature;
            try
            {
                signature = BuildSkillBarSignature();
            }
            catch
            {
                return;
            }

            if (signature == _barSignature) return;

            // A key held down under its old binding would never be released once the skill's key
            // changes, so let go of everything before rebuilding.
            ReleaseAllSkills();
            Initialize();
        }

        public void Initialize()
        {
            try
            {
                try { _barSignature = BuildSkillBarSignature(); } catch { _barSignature = null; }

                // Everything we have settings for: skills archived while they were off the bar, then
                // the ones currently listed on top (those are the live copies, so they win). A skill
                // that left the bar - the other weapon set, say - used to vanish from here and come
                // back later with default settings.
                var existingSkills = new Dictionary<string, ActiveSkill>();
                foreach (var skill in FollowHer.Instance.Settings.Combat.SkillArchive.Values)
                {
                    existingSkills[skill.Name] = skill;
                }
                foreach (var skill in FollowHer.Instance.Settings.Combat.Skills.Content)
                {
                    existingSkills[skill.Name] = skill;
                }

                var existingMovementSkills = new Dictionary<string, MovementSkill>();
                foreach (var skill in FollowHer.Instance.Settings.Movement.MovementSkillArchive.Values)
                {
                    existingMovementSkills[skill.Name] = skill;
                }
                foreach (var skill in FollowHer.Instance.Settings.Movement.MovementSkills.Content)
                {
                    existingMovementSkills[skill.Name] = skill;
                }

                _skills.Clear();
                _movementSkills.Clear();

                var skillBar = _gameController.IngameState.IngameUi.SkillBar;
                var shortcuts = _gameController.IngameState.ShortcutSettings.Shortcuts;

                for (var i = 0; i < Math.Min(13, skillBar.Skills.Count); i++)
                {
                    try
                    {
                        var skill = skillBar.Skills[i]?.Skill;
                        if (skill == null || skill.Address == 0)
                            continue;

                        // Bar slot i is shortcut Skill{i+1}. Checked against the live game: ShieldCharge
                        // sits in slots 1 and 6, and these read T and R - which is where it is bound.
                        // Shifting the index one further (slot 1 -> Skill3) lands on the right mouse
                        // button, whose virtual-key code is 2: the plugin then pressed RButton for a skill
                        // bound to T. Note MainKey is a ConsoleKey, so "1"/"2" are the left/right mouse
                        // buttons (VK 1/2), not the digit keys - parsing them as Keys is correct.
                        var shortcut = shortcuts.FirstOrDefault(x => x.Usage.ToString() == $"Skill{i + 1}");
                        if (!Enum.TryParse(shortcut.MainKey.ToString(), out System.Windows.Forms.Keys key))
                            continue;

                        if (IsMovementSkill(skill))
                        {
                            MovementSkill activeSkill;
                            if(existingMovementSkills.TryGetValue(skill.Name, out var existingMovementSkill))
                            {
                                activeSkill = existingMovementSkill;
                                activeSkill.Skill = skill;
                                activeSkill.Id = skill.Id;
                                activeSkill.InternalId = skill.Id2;
                                SyncKey(activeSkill, key);
                            }
                            else
                            {
                                activeSkill = new MovementSkill
                                {
                                    Skill = skill,
                                    Key = new HotkeyNode(key),
                                    Name = skill.Name,
                                    Id = skill.Id,
                                    InternalId = skill.Id2,
                                    Enabled = new ToggleNode(true),
                                    UseClick = new ToggleNode(false),
                                    ExtraDelay = new RangeNode<int>(0, 0, 5000),
                                    TravelsThroughObstacles = new ToggleNode(IsBlinkTypeMovementSkill(skill))
                                };
                            }

                            ApplyJumpDefaults(activeSkill, skill);
                            ApplyMovementSettingsMigration(activeSkill, skill);

                            _movementSkills[skill.Name] = activeSkill;
                        }
                        else
                        {
                            ActiveSkill activeSkill;
                            if (existingSkills.TryGetValue(skill.Name, out var existingSkill))
                            {
                                activeSkill = existingSkill;
                                activeSkill.Skill = skill;
                                activeSkill.Id = skill.Id;
                                activeSkill.InternalId = skill.Id2;
                                SyncKey(activeSkill, key);
                            }
                            else
                            {
                                activeSkill = new ActiveSkill
                                {
                                    Skill = skill,
                                    Key = new HotkeyNode(key),
                                    Name = skill.Name,
                                    Id = skill.Id,
                                    InternalId = skill.Id2,
                                    Enabled = new ToggleNode(true),
                                    UseClick = new ToggleNode(false),
                                    ExtraDelay = new RangeNode<int>(0, 0, 5000),
                                    LineOfSightType = new ListNode()
                                };
                            }

                            if (activeSkill.LineOfSightType.Values.Count == 0)
                            {
                                activeSkill.LineOfSightType.SetListValues(
                                [
                                    "Walkable",
                                    "Targetable (Can shoot over but not walk through)"
                                ]);
                            }

                            if (activeSkill.LineOfSightType.Value == null || activeSkill.LineOfSightType.Value == "")
                            {
                                activeSkill.LineOfSightType.Value = activeSkill.LineOfSightType.Values[1];
                            }

                            _skills[skill.Name] = activeSkill;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.LogError($"[SkillHandler] Error initializing skill {i}: {ex.Message}");
                    }
                }

                // Whatever has settings but isn't on the bar now goes to the archive, so it's still
                // there when the skill returns. Only done here, after a read that got this far, so a
                // failed refresh can't wipe it. Skills on the bar live in the visible lists instead.
                ArchiveOffBar(FollowHer.Instance.Settings.Combat.SkillArchive, existingSkills, _skills);
                ArchiveOffBar(FollowHer.Instance.Settings.Movement.MovementSkillArchive, existingMovementSkills, _movementSkills);

                FollowHer.Instance.Settings.Combat.Skills.Content.Clear();
                foreach (var skill in _skills.Values)
                {
                    FollowHer.Instance.Settings.Combat.Skills.Content.Add(skill);
                }

                FollowHer.Instance.Settings.Movement.MovementSkills.Content.Clear();
                foreach (var skill in _movementSkills.Values)
                {
                    FollowHer.Instance.Settings.Movement.MovementSkills.Content.Add(skill);
                }
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[SkillHandler] Error during initialization: {ex.Message}");
                if (ex.InnerException != null)
                {
                    DebugWindow.LogError($"[SkillHandler] Inner exception: {ex.InnerException.Message}");
                }
            }
        }

        public bool UseSkill(string skillName, bool force = false)
        {
            if (!_skills.TryGetValue(skillName, out var skill))
                return false;

            if (!force && !skill.CanUse)
                return false;

            try
            {
                if (skill.UseClick)
                    _keyHandler.SinglePress(skill.Key);
                else
                    _keyHandler.Hold(skill.Key);

                return true;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[SkillHandler] Error using skill {skillName}: {ex.Message}");
                return false;
            }
        }

        public bool UseMovementSkill(string skillName, bool force = false)
        {
            if (!_movementSkills.TryGetValue(skillName, out var skill))
                return false;
            if (!force && !skill.CanUse)
                return false;
            try
            {
                if (skill.UseClick)
                    _keyHandler.SinglePress(skill.Key);
                else
                    _keyHandler.Hold(skill.Key);
                return true;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[SkillHandler] Error using movement skill {skillName}: {ex.Message}");
                return false;
            }
        }

        public void ReleaseSkill(string skillName)
        {
            if (_skills.TryGetValue(skillName, out var skill))
            {
                _keyHandler.Release(skill.Key);
            }
        }

        public ActiveSkill GetSkill(string skillName)
        {
            return _skills.TryGetValue(skillName, out var skill) ? skill : null;
        }

        public void ReleaseAllSkills()
        {
            _keyHandler.ReleaseAll();
        }

        public IReadOnlyCollection<ActiveSkill> GetAllSkills() => _skills.Values.Where(s => s.Enabled).ToList();

        // Every name a combat rule's SkillName is matched against - used by the combat diagnostic
        // to show what the skill bar actually reported when a rule fails to resolve.
        public IReadOnlyCollection<string> GetAllSkillNames() => _skills.Keys.ToList();

        public void Dispose()
        {
            ReleaseAllSkills();
            _keyHandler.Dispose();
            _skills.Clear();
        }
    }
}