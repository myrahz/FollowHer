using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using FollowHer.Core.Events;
using FollowHer.Core.Events.Events;
using Graphics = ExileCore.Graphics;

namespace FollowHer.Utils
{
    public enum LineOfSightDataType
    {
        Terrain,
        Walkable
    }

    public class LineOfSight
    {
        private readonly GameController _gameController;
        private int[][] _terrainData;
        private int[][] _walkableData;
        private Vector2 _areaDimensions;
        private const int TARGET_LAYER_VALUE = 4;

        private readonly List<(Vector2 Pos, int Value)> _debugPoints = new();
        private readonly List<(Vector2 Start, Vector2 End, bool IsVisible)> _debugRays = new();
        private readonly HashSet<Vector2> _debugVisiblePoints = new();

        // Whether the ray walkers below should record the cells they visit. They're on the hot path -
        // path smoothing alone fires thousands of rays per route computation - and _debugVisiblePoints
        // is only ever cleared while the debug overlay is on, so recording unconditionally both cost
        // time on every step and grew the set for the whole session when nothing would ever read it.
        private bool _recordDebugPoints;
        private float _lastObserverZ;
        private Vector2 _lastDebugGridCenter;

        // The area the cached grids were read for, so a stale one is never answered from.
        private const int ValidationIntervalMs = 250;
        private const int PopulateRetryIntervalMs = 500;
        private uint _dataAreaHash;
        private DateTime _nextValidationAt = DateTime.MinValue;
        private DateTime _nextPopulateRetryAt = DateTime.MinValue;

        public LineOfSight(GameController gameController)
        {
            _gameController = gameController;

            var eventBus = EventBus.Instance;
            eventBus.Subscribe<AreaChangeEvent>(HandleAreaChange);
            eventBus.Subscribe<RenderEvent>(HandleRender);

            // Populate immediately for the *current* area rather than waiting for the next
            // AreaChangeEvent - otherwise a freshly-constructed instance (e.g. right after
            // switching combat strategy mid-map) has blank terrain data and HasLineOfSight
            // returns false unconditionally until the player next changes zones.
            try
            {
                PopulateTerrainData();
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[LineOfSight] Error populating initial terrain data: {ex.Message}");
            }
        }

        public void Dispose()
        {
            var eventBus = EventBus.Instance;
            eventBus.Unsubscribe<AreaChangeEvent>(HandleAreaChange);
            eventBus.Unsubscribe<RenderEvent>(HandleRender);
        }

        private void HandleRender(RenderEvent evt)
        {
            if (!FollowHer.Instance.Settings.Render.EnableRendering) return;
            if (!FollowHer.Instance.Settings.Render.ShowTerrainDebug && !FollowHer.Instance.Settings.Render.ShowWalkableDebug) return;

            if (_terrainData == null)
            {
                return;
            }

            var playerGridPos = _gameController.Player.GridPosNum;
            if (Vector2.DistanceSquared(playerGridPos, _lastDebugGridCenter) >= 20) // dont update too frequently
            {
                var losType = FollowHer.Instance.Settings.Render.ShowWalkableDebug ? LineOfSightDataType.Walkable : LineOfSightDataType.Terrain;
                UpdateDebugGrid(playerGridPos, losType);
            }

            foreach (var (pos, value) in _debugPoints)
            {
                var worldPos = new Vector3(pos.GridToWorld(), _lastObserverZ);
                var screenPos = _gameController.IngameState.Camera.WorldToScreen(worldPos);

                SharpDX.Color color; // Change type to SharpDX.Color
                if (_debugVisiblePoints.Contains(pos))
                {
                    color = SharpDX.Color.Yellow; // Use SharpDX.Color
                }
                else
                {
                    // Colors here are per-raw-value labels only, not a blocking/passable verdict -
                    // see IsBlocking for the actual rule per data type. For Walkable specifically,
                    // only 0 blocks (confirmed against Radar's own working pathfinder); 1-5 are all
                    // passable despite the "obstacle"-sounding labels below.
                    color = value switch
                    {
                        0 => new SharpDX.Color(0, 128, 0, 128),    // value 0
                        1 => new SharpDX.Color(0, 0, 128, 128),    // value 1
                        2 => new SharpDX.Color(255, 165, 0, 128),  // value 2
                        3 => new SharpDX.Color(255, 0, 0, 128),    // value 3
                        4 => new SharpDX.Color(128, 0, 128, 128),  // value 4
                        5 => new SharpDX.Color(0, 0, 0, 128),      // value 5
                        _ => new SharpDX.Color(128, 128, 128, 128) // unknown
                    };
                }

                evt.Graphics.DrawText(
                    value.ToString(),
                    screenPos,
                    color,
                    FontAlign.Center
                );
            }
        }
        
        private void HandleAreaChange(AreaChangeEvent evt)
        {
            // Drop the old area's grids before trying to read the new ones. If the read fails (the
            // game often isn't done writing terrain the instant the area-change fires), having *no*
            // data is recoverable - EnsureCurrentAreaData retries and callers fall back to walking
            // straight. Keeping the previous zone's grid is not: it silently pathfinds on the wrong
            // map, which looks like the character deciding to walk into a wall.
            _terrainData = null;
            _walkableData = null;
            _dataAreaHash = 0;
            _nextPopulateRetryAt = DateTime.MinValue;
            _nextValidationAt = DateTime.MinValue;

            PopulateTerrainData();
        }

        /// <summary>Makes sure the cached grids belong to the area we're standing in, repopulating if
        /// they don't. Nothing else validated this: the data was only ever refreshed by the area-change
        /// event, so one failed refresh left every later query answering from the previous zone.
        /// The area hash is only re-read every <see cref="ValidationIntervalMs"/> because this sits on
        /// the path-smoothing hot path, which asks thousands of times per route.</summary>
        private bool EnsureCurrentAreaData()
        {
            var now = DateTime.Now;
            var haveData = _terrainData != null && _walkableData != null;
            if (haveData && now < _nextValidationAt) return true;

            uint areaHash;
            try
            {
                areaHash = _gameController.Game?.CurrentAreaHash ?? 0;
            }
            catch
            {
                return haveData;
            }

            _nextValidationAt = now.AddMilliseconds(ValidationIntervalMs);
            if (haveData && _dataAreaHash == areaHash) return true;

            if (now < _nextPopulateRetryAt) return false;
            _nextPopulateRetryAt = now.AddMilliseconds(PopulateRetryIntervalMs);
            return PopulateTerrainData();
        }

        /// <summary>Reads both terrain layers for the current area. Everything is built into locals and
        /// published in one go at the end: a half-applied refresh (new targeting layer, previous
        /// zone's walkable layer) is worse than no refresh at all, and the assignments used to be
        /// interleaved with the reads that can throw.</summary>
        private bool PopulateTerrainData()
        {
            try
            {
                var areaHash = _gameController.Game?.CurrentAreaHash ?? 0;
                var dimensions = _gameController.IngameState.Data.AreaDimensions;
                var rawTargetingData = _gameController.IngameState.Data.RawTerrainTargetingData;
                var rawWalkableData = _gameController.IngameState.Data.RawPathfindingData;

                if (rawTargetingData == null || rawWalkableData == null)
                {
                    DebugWindow.LogError("[LineOfSight] Terrain data isn't available yet - will retry");
                    return false;
                }

                var terrainData = CopyGrid(rawTargetingData);
                var walkableData = CopyGrid(rawWalkableData);

                _areaDimensions = dimensions;
                _terrainData = terrainData;
                _walkableData = walkableData;
                _dataAreaHash = areaHash;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[LineOfSight] Failed to read terrain data: {ex.Message} - will retry");
                return false;
            }

            var debugVisualsEnabled = FollowHer.Instance.Settings.Render.EnableRendering &&
                                       (FollowHer.Instance.Settings.Render.ShowTerrainDebug ||
                                        FollowHer.Instance.Settings.Render.ShowWalkableDebug);

            if (debugVisualsEnabled && _gameController.Player != null)
            {
                UpdateDebugGrid(_gameController.Player.GridPosNum, LineOfSightDataType.Terrain);
            }

            return true;
        }

        private static int[][] CopyGrid(int[][] source)
        {
            var copy = new int[source.Length][];
            Parallel.For(0, source.Length, y =>
            {
                var row = source[y];
                if (row == null) return;
                copy[y] = new int[row.Length];
                Array.Copy(row, copy[y], row.Length);
            });

            return copy;
        }

        private void UpdateDebugGrid(Vector2 center, LineOfSightDataType losType)
        {
            _debugPoints.Clear();
            _lastDebugGridCenter = center;
            const int size = 200;

            for (var y = -size; y <= size; y++)
                for (var x = -size; x <= size; x++)
                {
                    if (x * x + y * y > size * size) continue;

                    var pos = new Vector2(center.X + x, center.Y + y);
                    var value = GetValue(pos, losType);
                    if (value >= 0) _debugPoints.Add((pos, value));
                }

            _lastObserverZ = _gameController.IngameState.Data.GetTerrainHeightAt(center);
        }

        public bool HasLineOfSight(Vector2 start, Vector2 end)
        {
            return HasLineOfSight(start, end, LineOfSightDataType.Terrain);
        }

        public bool HasLineOfSight(Vector2 start, Vector2 end, LineOfSightDataType losType)
        {
            // Same check the grid accessors make - answering a sight query from the previous zone's
            // terrain is how a route ends up walking into a wall.
            if (!EnsureCurrentAreaData()) return false;
            if (_terrainData == null) return false;

            // Debug visualization bookkeeping (UpdateDebugGrid is an O(size^2) rebuild) is only
            // worth paying for when the debug overlay is actually being rendered - this method is
            // called frequently (pathing/LOS checks every tick) so skip it otherwise.
            var debugVisualsEnabled = FollowHer.Instance.Settings.Render.EnableRendering &&
                                       (FollowHer.Instance.Settings.Render.ShowTerrainDebug ||
                                        FollowHer.Instance.Settings.Render.ShowWalkableDebug);

            _recordDebugPoints = debugVisualsEnabled;

            if (debugVisualsEnabled)
            {
                _debugVisiblePoints.Clear();
                UpdateDebugGrid(start, losType);
            }

            var isVisible = HasLineOfSightInternal(start, end, losType);

            if (debugVisualsEnabled)
            {
                _debugRays.Add((start, end, isVisible));
            }

            return isVisible;
        }

        private bool HasLineOfSightInternal(Vector2 start, Vector2 end, LineOfSightDataType losType)
        {
            var startX = (int)start.X;
            var startY = (int)start.Y;
            var endX = (int)end.X;
            var endY = (int)end.Y;

            if (!IsInBounds(startX, startY) || !IsInBounds(endX, endY))
                return false;

            var dx = Math.Abs(endX - startX);
            var dy = Math.Abs(endY - startY);

            if (dx == 0)
                return CheckVerticalLine(startX, startY, endY, losType);
            if (dy == 0)
                return CheckHorizontalLine(startY, startX, endX, losType);

            return CheckDiagonalLine(start, end, dx, dy, losType);
        }

        // Only the Walkable layer (RawPathfindingData) has a confirmed encoding: verified against
        // the Radar plugin's own working pathfinder (PathFinder.cs, constructed with pathable
        // values {1,2,3,4,5}) reading the exact same raw source - value 0 is the only blocking
        // value, everything else is passable. The Terrain layer (RawTerrainTargetingData, used
        // for attack-skill targeting LOS) has no such confirmation either way, so it keeps the
        // prior threshold rather than guessing.
        private static bool IsBlocking(int value, LineOfSightDataType losType)
        {
            if (value < 0) return true; // out-of-bounds sentinel from GetValue

            if (losType == LineOfSightDataType.Walkable)
            {
                return value == 0;
            }

            return value >= TARGET_LAYER_VALUE;
        }

        private bool CheckVerticalLine(int x, int startY, int endY, LineOfSightDataType losType)
        {
            var step = Math.Sign(endY - startY);
            var y = startY;

            while (y != endY)
            {
                y += step;
                var pos = new Vector2(x, y);
                var terrainValue = GetValue(pos, losType);
                if (_recordDebugPoints) _debugVisiblePoints.Add(pos);

                if (IsBlocking(terrainValue, losType)) return false;
            }

            return true;
        }

        private bool CheckHorizontalLine(int y, int startX, int endX, LineOfSightDataType losType)
        {
            var step = Math.Sign(endX - startX);
            var x = startX;

            while (x != endX)
            {
                x += step;
                var pos = new Vector2(x, y);
                var terrainValue = GetValue(pos, losType);
                if (_recordDebugPoints) _debugVisiblePoints.Add(pos);

                if (IsBlocking(terrainValue, losType)) return false;
            }

            return true;
        }

        private bool CheckDiagonalLine(Vector2 start, Vector2 end, float dx, float dy, LineOfSightDataType losType)
        {
            var x = (int)start.X;
            var y = (int)start.Y;
            var stepX = Math.Sign(end.X - start.X);
            var stepY = Math.Sign(end.Y - start.Y);

            if (dx >= dy)
            {
                var deltaError = dy / dx;
                var error = 0.0f;

                while (x != (int)end.X)
                {
                    x += stepX;
                    error += deltaError;

                    if (error >= 0.5f)
                    {
                        y += stepY;
                        error -= 1.0f;
                    }

                    var pos = new Vector2(x, y);
                    var terrainValue = GetValue(pos, losType);
                    if (_recordDebugPoints) _debugVisiblePoints.Add(pos);

                    if (IsBlocking(terrainValue, losType)) return false;
                }
            }
            else
            {
                var deltaError = dx / dy;
                var error = 0.0f;

                while (y != (int)end.Y)
                {
                    y += stepY;
                    error += deltaError;

                    if (error >= 0.5f)
                    {
                        x += stepX;
                        error -= 1.0f;
                    }

                    var pos = new Vector2(x, y);
                    var terrainValue = GetValue(pos, losType);
                    if (_recordDebugPoints) _debugVisiblePoints.Add(pos);

                    if (IsBlocking(terrainValue, losType)) return false;
                }
            }

            return true;
        }

        private bool IsInBounds(int x, int y)
        {
            return x >= 0 && x < _areaDimensions.X && y >= 0 && y < _areaDimensions.Y;
        }

        private int GetValue(Vector2 position, LineOfSightDataType losType)
        {
            var x = (int)position.X;
            var y = (int)position.Y;

            if (!IsInBounds(x, y)) return -1;

            return losType switch
            {
                LineOfSightDataType.Terrain => _terrainData[y][x],
                LineOfSightDataType.Walkable => _walkableData[y][x],
                _ => -1
            };
        }

        public static LineOfSightDataType Parse(string type)
        {
            return type switch
            {
                "Walkable" => LineOfSightDataType.Walkable,
                _ => LineOfSightDataType.Terrain
            };
        }

        public Vector2 AreaDimensions => _areaDimensions;

        /// <summary>The live grid, without copying. For read-only use - the pathfinder and the
        /// clearance checks only ever read it, and <see cref="GetGrid"/>'s deep copy is 1771x2048
        /// ints (~14 MB) on a normal area, which it was paying on every path recompute and every
        /// line-of-sight-grid query. Callers must not write to what they get back.</summary>
        public int[][] GetGridReference(LineOfSightDataType losType = LineOfSightDataType.Walkable)
        {
            if (!EnsureCurrentAreaData()) return null;
            return losType == LineOfSightDataType.Walkable ? _walkableData : _terrainData;
        }

        /// <summary>A private copy of the grid, safe to mutate. Prefer
        /// <see cref="GetGridReference"/> unless you actually need to write to it.</summary>
        public int[][] GetGrid(LineOfSightDataType losType = LineOfSightDataType.Walkable)
        {
            if (!EnsureCurrentAreaData()) return null;

            var source = losType == LineOfSightDataType.Walkable ? _walkableData : _terrainData;
            if (source == null) return null;

            var copy = new int[source.Length][];
            for (var y = 0; y < source.Length; y++)
            {
                if (source[y] == null) continue;
                copy[y] = new int[source[y].Length];
                Array.Copy(source[y], copy[y], source[y].Length);
            }

            return copy;
        }

        public void Clear()
        {
            _terrainData = null;
            _walkableData = null;
            _dataAreaHash = 0;
            _debugPoints.Clear();
            _debugRays.Clear();
            _debugVisiblePoints.Clear();
        }
    }
}