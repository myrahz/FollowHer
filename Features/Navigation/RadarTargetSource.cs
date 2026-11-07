using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore;
using Newtonsoft.Json.Linq;

namespace FollowHer.Features.Navigation;

/// <summary>Reads the target list from the Radar plugin instead of maintaining a second one.
///
/// Radar doesn't expose its target list over the plugin bridge - it only registers
/// "Radar.ClusterTarget" (a tile-name pattern + expected count -> clustered grid positions) and
/// "Radar.LookForRoute". So the *names* come from Radar's own targets.json (the file that defines
/// what it marks, keyed by area with a global "*" block), and the *positions* come from Radar itself
/// through the bridge, which means they're the exact cells Radar draws on the map - clustered by its
/// KMeans and snapped to walkable tiles by its own code. Nothing is duplicated here but the file
/// parse, and the same technique is already used by another plugin in this install
/// (AtlasHelper/ExileCampaigns).
///
/// Two Radar features the bridge signature can't carry: a target's "Rooms" filter is ignored
/// (ClusterTarget takes no rooms argument), and entries whose Name is "*" (room-scoped only) are
/// skipped since there's no tile pattern to cluster. Everything else - including entity-path targets,
/// whose positions Radar folds into the same lookup as tiles once the entity loads - resolves
/// normally.</summary>
public class RadarTargetSource
{
    private const string ClusterTargetBridgeMethod = "Radar.ClusterTarget";

    // A scan that finds nothing usually means Radar hasn't processed the new area yet, so an empty
    // result is retried rather than cached for the rest of the zone. Bounded, because an area where
    // Radar genuinely marks nothing would otherwise retry forever on the render thread - past the
    // cap, the Refresh button is the way to scan again.
    private const int EmptyRescanDelayMs = 2000;
    private const int MaxEmptyRescans = 10;

    private Func<string, int, Vector2[]> _clusterTarget;
    private List<NavTarget> _targets = new();

    // targets.json is parsed once per area and reused across rescans - only the (cheap) bridge calls
    // are repeated when a rescan is needed because Radar wasn't ready yet.
    private List<TargetEntry> _entries;
    private string _entriesAreaName;

    private uint _scannedAreaHash;
    private bool _hasScanned;
    private int _emptyRescans;
    private DateTime _nextAutoScanAt = DateTime.MinValue;

    public IReadOnlyList<NavTarget> Targets => _targets;

    /// <summary>What happened on the last scan - shown in the navigator window so a missing Radar or
    /// an unreadable targets.json is visible rather than just an empty list.</summary>
    public string Status { get; private set; } = "not scanned yet";

    public bool IsRadarConnected => _clusterTarget != null;

    /// <summary>Rescans if the area changed since the last scan (or if the last scan came up empty
    /// and the retry delay has elapsed). Cheap when there's nothing to do, so it's safe to call every
    /// frame the window is open.</summary>
    public void EnsureScanned(GameController gameController)
    {
        var areaHash = gameController?.Game?.CurrentAreaHash ?? 0;

        if (_hasScanned && _scannedAreaHash == areaHash)
        {
            if (_targets.Count > 0 || _emptyRescans >= MaxEmptyRescans || DateTime.Now < _nextAutoScanAt) return;
            _emptyRescans++;
        }

        ScanInternal(gameController);
    }

    /// <summary>Explicit rescan (the Refresh button): also re-reads targets.json, so edits to it show
    /// up without a zone change.</summary>
    public void Scan(GameController gameController)
    {
        _entries = null;
        _emptyRescans = 0;
        ScanInternal(gameController);
    }

    private void ScanInternal(GameController gameController)
    {
        var areaHash = gameController?.Game?.CurrentAreaHash ?? 0;
        if (areaHash != _scannedAreaHash) _emptyRescans = 0;

        _hasScanned = true;
        _scannedAreaHash = areaHash;
        _nextAutoScanAt = DateTime.Now.AddMilliseconds(EmptyRescanDelayMs);

        try
        {
            _targets = BuildTargets(gameController);
        }
        catch (Exception ex)
        {
            _targets = new List<NavTarget>();
            Status = $"scan failed: {ex.Message}";
        }
    }

    public void Invalidate()
    {
        _hasScanned = false;
        _targets = new List<NavTarget>();
        _entries = null;
        _emptyRescans = 0;
        Status = "waiting for area data";
    }

    private List<NavTarget> BuildTargets(GameController gameController)
    {
        var results = new List<NavTarget>();

        // targets.json is keyed by the area's raw id ("2_8_14", "MapWorldsCitadel"), which is what
        // Radar matches its keys against - WorldArea.Id is that same string (RawName is its obsolete
        // alias), so the lookup here resolves exactly as Radar's does.
        var areaName = gameController?.Area?.CurrentArea?.Area?.Id;
        if (string.IsNullOrEmpty(areaName))
        {
            Status = "not in an area yet";
            return results;
        }

        if (_clusterTarget == null)
        {
            try
            {
                _clusterTarget = gameController.PluginBridge.GetMethod<Func<string, int, Vector2[]>>(ClusterTargetBridgeMethod);
            }
            catch
            {
                _clusterTarget = null;
            }
        }

        if (_clusterTarget == null)
        {
            Status = "Radar not loaded (no 'Radar.ClusterTarget' on the plugin bridge)";
            return results;
        }

        if (_entries == null || _entriesAreaName != areaName)
        {
            var targetsFile = FindRadarTargetsFile();
            if (targetsFile == null)
            {
                Status = "Radar's targets.json not found";
                return results;
            }

            _entries = ParseEntriesForArea(File.ReadAllText(targetsFile), areaName);
            _entriesAreaName = areaName;
        }

        if (_entries.Count == 0)
        {
            Status = $"no targets defined for '{areaName}'";
            return results;
        }

        foreach (var entry in _entries)
        {
            // Radar tries a target's alternatives in order and keeps the first that resolves - match
            // that, so an area that uses an alternative tile name still produces the same marker.
            foreach (var (pattern, expectedCount) in entry.Patterns)
            {
                Vector2[] locations;
                try
                {
                    locations = _clusterTarget(pattern, expectedCount);
                }
                catch
                {
                    // Radar throws while an area is still loading (terrain arrays not populated) -
                    // treat it as "not resolvable yet" and let the next scan pick it up.
                    continue;
                }

                if (locations is not { Length: > 0 }) continue;

                for (var i = 0; i < locations.Length; i++)
                {
                    var label = locations.Length > 1 ? $"{entry.Label} #{i + 1}" : entry.Label;
                    results.Add(new NavTarget(label, pattern, locations[i]));
                }

                break;
            }
        }

        Status = results.Count > 0
            ? $"{results.Count} target(s) in '{areaName}'"
            : $"Radar resolved none of the {_entries.Count} target(s) defined for '{areaName}'";

        return results;
    }

    private sealed class TargetEntry
    {
        public string Label;

        // The primary (Name, ExpectedCount) followed by the entry's alternatives, in Radar's order.
        public readonly List<(string pattern, int expectedCount)> Patterns = new();
    }

    private static List<TargetEntry> ParseEntriesForArea(string json, string areaName)
    {
        var entries = new List<TargetEntry>();
        var seenPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch
        {
            return entries;
        }

        void AddBlock(JArray block)
        {
            foreach (var element in block.OfType<JObject>())
            {
                var entry = new TargetEntry
                {
                    Label = (string)element["DisplayName"] is { Length: > 0 } display
                        ? display
                        : Leaf((string)element["Name"]),
                };

                AddPattern(entry, element);
                if (element["Alternatives"] is JArray alternatives)
                {
                    foreach (var alternative in alternatives.OfType<JObject>())
                    {
                        AddPattern(entry, alternative);
                    }
                }

                // Only ever a duplicate row otherwise: the same tile pattern resolves to the same
                // clusters no matter which entry asked for it.
                if (entry.Patterns.Count > 0 && seenPatterns.Add(entry.Patterns[0].pattern))
                {
                    entries.Add(entry);
                }
            }
        }

        // Keys are area RawName globs ("1_1_1", "MapWorlds*"), matched the way Radar matches them.
        foreach (var property in root.Properties())
        {
            if (property.Name == "*") continue;
            if (property.Value is JArray block && LikeRegex(property.Name).IsMatch(areaName))
            {
                AddBlock(block);
            }
        }

        // The global block applies to every area (league mechanics, boss rooms, ...).
        if (root["*"] is JArray globalBlock) AddBlock(globalBlock);

        return entries;
    }

    private static void AddPattern(TargetEntry entry, JObject element)
    {
        var name = (string)element["Name"];

        // Name "*" is a room-scoped target: there's no tile pattern for ClusterTarget to work with,
        // and the bridge can't take the room filter that would give it meaning.
        if (string.IsNullOrWhiteSpace(name) || name == "*") return;

        var expectedCount = (int?)element["ExpectedCount"] ?? 1;
        entry.Patterns.Add((name, Math.Max(1, expectedCount)));
    }

    /// <summary>Radar's glob matching: "*" is any sequence, "?" is any single character.</summary>
    private static Regex LikeRegex(string pattern)
    {
        return new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string Leaf(string name)
    {
        if (string.IsNullOrEmpty(name)) return "(unnamed)";

        var leaf = name[(name.LastIndexOf('/') + 1)..];
        if (leaf.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^4];
        return leaf.Length > 0 ? leaf : name;
    }

    /// <summary>Locates Radar's targets.json. Probed relative to the host's directory rather than
    /// asked of Radar, since the bridge exposes no such method - covers both a source install
    /// (Plugins/Source/Radar) and a compiled one (Plugins/Compiled/Radar), and an explicit override
    /// in the settings wins over both.</summary>
    private static string FindRadarTargetsFile()
    {
        var configured = FollowHer.Instance.Settings.Navigation.RadarTargetsPath.Value;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var root = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(root, "Plugins", "Source", "Radar", "targets.json"),
            Path.Combine(root, "Plugins", "Compiled", "Radar", "targets.json"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
