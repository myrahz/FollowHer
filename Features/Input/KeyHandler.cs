using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore;

namespace FollowHer.Features.Input
{
    public class KeyHandler : IDisposable
    {
        private class KeyState
        {
            public Keys Key { get; }
            public DateTime LastPressed { get; private set; }
            public bool IsHeld { get; private set; }

            public KeyState(Keys key)
            {
                Key = key;
                LastPressed = DateTime.Now;
                IsHeld = false;
            }

            public void Press()
            {
                LastPressed = DateTime.Now;
                IsHeld = true;
            }

            public void Release()
            {
                IsHeld = false;
            }
        }

        private readonly Dictionary<Keys, KeyState> _activeKeys = new();
        private bool _isDisposed;
        private const int SINGLE_PRESS_DELAY = 5;

        public void Hold(Keys key)
        {
            if (_isDisposed) return;

            try
            {
                if (!_activeKeys.TryGetValue(key, out var state))
                {
                    state = new KeyState(key);
                    _activeKeys[key] = state;
                }

                if (!state.IsHeld)
                {
                    ExileCore.Input.KeyDown(key);
                    state.Press();
                }
            }
            catch (Exception ex)
            {
                LogError($"Error holding key {key}: {ex.Message}");
                ReleaseAll();
            }
        }

        public void Release(Keys key)
        {
            if (_isDisposed) return;

            try
            {
                if (_activeKeys.TryGetValue(key, out var state) && state.IsHeld)
                {
                    ExileCore.Input.KeyUp(key);
                    state.Release();
                }
            }
            catch (Exception ex)
            {
                LogError($"Error releasing key {key}: {ex.Message}");
            }
        }

        public void SinglePress(Keys key)
        {
            if (_isDisposed) return;

            try
            {
                Hold(key);
                System.Threading.Thread.Sleep(SINGLE_PRESS_DELAY);
                Release(key);
            }
            catch (Exception ex)
            {
                LogError($"Error single pressing key {key}: {ex.Message}");
                ReleaseAll();
            }
        }

        public void ReleaseAll()
        {
            if (_isDisposed) return;

            foreach (var state in _activeKeys.Values)
            {
                try
                {
                    if (state.IsHeld)
                    {
                        ExileCore.Input.KeyUp(state.Key);
                        state.Release();
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Error releasing key {state.Key}: {ex.Message}");
                }
            }

            _activeKeys.Clear();
        }

        public bool IsKeyHeld(Keys key)
        {
            return _activeKeys.TryGetValue(key, out var state) && state.IsHeld;
        }

        public DateTime GetLastPressTime(Keys key)
        {
            return _activeKeys.TryGetValue(key, out var state) ? state.LastPressed : DateTime.MinValue;
        }

        private void LogError(string message)
        {
            DebugWindow.LogError($"[KeyHandler] {message}");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    try
                    {
                        ReleaseAll();
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error disposing: {ex.Message}");
                    }
                }
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~KeyHandler()
        {
            Dispose(false);
        }
    }
}