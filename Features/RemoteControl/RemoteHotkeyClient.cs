using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using Newtonsoft.Json;

namespace FollowHer.Features.RemoteControl;

/// <summary>Follower-only reconnecting client for FollowHerLeader.</summary>
public sealed class RemoteHotkeyClient : IDisposable
{
    private sealed class Message
    {
        public string Type { get; set; }
        public string FollowerName { get; set; }
        public int Key { get; set; }
        public bool Enabled { get; set; }
        public string CommandId { get; set; }
    }

    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentQueue<(string CommandId, Keys Key)> _receivedKeyCommands = new();
    private readonly ConcurrentQueue<(string CommandId, bool Enabled)> _movementStateCommands = new();
    private readonly ConcurrentDictionary<string, byte> _queuedCommands = new();
    private readonly ConcurrentDictionary<string, byte> _completedCommands = new();
    private readonly ConcurrentQueue<string> _completedCommandOrder = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _writerLock = new();
    private readonly string _leaderAddress;
    private readonly int _port;
    private StreamWriter _writer;
    private string _followerName = string.Empty;
    private string _lastConnectionIssue = string.Empty;
    private DateTime _lastConnectionIssueAt = DateTime.MinValue;
    private bool _disposed;

    public RemoteHotkeyClient(string leaderAddress, int port)
    {
        _leaderAddress = leaderAddress.Trim();
        _port = port;
        LogInfo($"Starting follower client for {_leaderAddress}:{_port}.");
        _ = RunAsync();
    }

    public void UpdateFollowerName(string followerName)
    {
        if (string.IsNullOrWhiteSpace(followerName)) return;
        followerName = followerName.Trim();
        if (string.Equals(_followerName, followerName, StringComparison.Ordinal)) return;
        _followerName = followerName;
        SendRegistration();
    }

    public bool TryDequeueKey(out string commandId, out Keys key)
    {
        if (_receivedKeyCommands.TryDequeue(out var command))
        {
            commandId = command.CommandId;
            key = command.Key;
            return true;
        }
        commandId = null;
        key = Keys.None;
        return false;
    }

    public bool TryDequeueMovementState(out string commandId, out bool enabled)
    {
        if (_movementStateCommands.TryDequeue(out var command))
        {
            commandId = command.CommandId;
            enabled = command.Enabled;
            return true;
        }
        commandId = null;
        enabled = false;
        return false;
    }

    public void AcknowledgeExecutedCommand(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;
        _queuedCommands.TryRemove(commandId, out _);
        RememberCompletedCommand(commandId);
        _ = SendAsync(new Message { Type = "ack", CommandId = commandId });
    }

    private async Task RunAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                await client.ConnectAsync(_leaderAddress, _port, _cancellation.Token).ConfigureAwait(false);
                LogInfo($"Connected to FollowHerLeader at {_leaderAddress}:{_port}.");
                _lastConnectionIssue = string.Empty;
                var stream = client.GetStream();
                var reader = new StreamReader(stream);
                var writer = new StreamWriter(stream) { AutoFlush = true };
                lock (_writerLock) _writer = writer;
                SendRegistration();

                while (!_cancellation.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                    if (line == null) break;
                    var message = JsonConvert.DeserializeObject<Message>(line);

                    if (message?.Type == "registered")
                    {
                        LogInfo($"Leader acknowledged follower '{message.FollowerName}'. Remote commands are ready.");
                    }
                    else if (message?.Type == "key" && message.Key > 0)
                    {
                        if (!QueueCommandOrAcknowledgeDuplicate(message)) continue;
                        var key = (Keys)message.Key;
                        LogInfo($"Received key {key} from leader; queued for input.");
                        _receivedKeyCommands.Enqueue((message.CommandId, key));
                    }
                    else if (message?.Type == "setMovement")
                    {
                        if (!QueueCommandOrAcknowledgeDuplicate(message)) continue;
                        LogInfo($"Received movement {(message.Enabled ? "ON" : "OFF")} from leader; queued for state update.");
                        _movementStateCommands.Enqueue((message.CommandId, message.Enabled));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                LogConnectionIssue(ex.Message);
            }
            catch (Exception ex)
            {
                LogError($"Follower client failed: {ex.Message}");
            }
            finally
            {
                lock (_writerLock) _writer = null;
                client?.Dispose();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    private void SendRegistration()
    {
        if (!string.IsNullOrWhiteSpace(_followerName))
        {
            _ = SendAsync(new Message { Type = "register", FollowerName = _followerName });
        }
    }

    private async Task SendAsync(Message message)
    {
        StreamWriter writer;
        lock (_writerLock) writer = _writer;
        if (writer == null) return;

        try
        {
            await _writeLock.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(JsonConvert.SerializeObject(message)).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            if (message.Type == "register")
            {
                LogInfo($"Sent registration for follower '{message.FollowerName}'; waiting for acknowledgement.");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private bool QueueCommandOrAcknowledgeDuplicate(Message message)
    {
        if (string.IsNullOrWhiteSpace(message.CommandId)) return false;
        if (_completedCommands.ContainsKey(message.CommandId))
        {
            _ = SendAsync(new Message { Type = "ack", CommandId = message.CommandId });
            return false;
        }
        return _queuedCommands.TryAdd(message.CommandId, 0);
    }

    private void RememberCompletedCommand(string commandId)
    {
        if (!_completedCommands.TryAdd(commandId, 0)) return;
        _completedCommandOrder.Enqueue(commandId);
        while (_completedCommandOrder.Count > 256 && _completedCommandOrder.TryDequeue(out var oldest))
        {
            _completedCommands.TryRemove(oldest, out _);
        }
    }

    private void LogConnectionIssue(string message)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(_lastConnectionIssue, message, StringComparison.Ordinal) &&
            now - _lastConnectionIssueAt < TimeSpan.FromSeconds(10)) return;
        _lastConnectionIssue = message;
        _lastConnectionIssueAt = now;
        LogInfo($"Could not connect to {_leaderAddress}:{_port}: {message}. Retrying every 2 seconds.");
    }

    private static void LogInfo(string message) => DebugWindow.LogMsg($"[RemoteHotkeys] {message}");
    private static void LogError(string message) => DebugWindow.LogError($"[RemoteHotkeys] {message}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        lock (_writerLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _writeLock.Dispose();
        _cancellation.Dispose();
    }
}
