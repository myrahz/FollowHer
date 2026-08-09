using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using Newtonsoft.Json;

namespace FollowHer.Features.RemoteControl;

/// <summary>
/// Small newline-delimited JSON transport for leader-to-follower key presses. A blank leader
/// address starts the server; a non-blank address starts a reconnecting follower client.
/// Game state and input are deliberately kept off the networking threads.
/// </summary>
public sealed class RemoteHotkeyNetwork : IDisposable
{
    private sealed class Message
    {
        public string Type { get; set; }
        public string FollowerName { get; set; }
        public int Key { get; set; }
    }

    private sealed class FollowerConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public string Name { get; set; }

        public FollowerConnection(TcpClient client, StreamWriter writer)
        {
            _client = client;
            _writer = writer;
        }

        public async Task SendAsync(Message message, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(JsonConvert.SerializeObject(message)).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _writer.Dispose();
            _client.Dispose();
            _writeLock.Dispose();
        }
    }

    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, FollowerConnection> _followers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Keys> _receivedKeys = new();
    private readonly object _clientWriterLock = new();
    private readonly string _leaderAddress;
    private readonly int _port;
    private TcpListener _listener;
    private StreamWriter _clientWriter;
    private string _followerName = string.Empty;
    private string _lastConnectionIssue = string.Empty;
    private DateTime _lastConnectionIssueAt = DateTime.MinValue;
    private bool _disposed;

    public bool IsLeader => string.IsNullOrWhiteSpace(_leaderAddress);

    public RemoteHotkeyNetwork(string leaderAddress, int port)
    {
        _leaderAddress = leaderAddress?.Trim() ?? string.Empty;
        _port = port;
        LogInfo(IsLeader
            ? $"Starting leader server on TCP port {_port}."
            : $"Starting follower client for {_leaderAddress}:{_port}.");
        _ = IsLeader ? RunServerAsync() : RunClientAsync();
    }

    public void UpdateFollowerName(string followerName)
    {
        if (IsLeader || string.IsNullOrWhiteSpace(followerName)) return;

        followerName = followerName.Trim();
        if (string.Equals(_followerName, followerName, StringComparison.Ordinal)) return;

        _followerName = followerName;
        SendRegistration();
    }

    public bool TryDequeueKey(out Keys key) => _receivedKeys.TryDequeue(out key);

    public void SendKey(string followerName, Keys key)
    {
        if (!IsLeader || string.IsNullOrWhiteSpace(followerName) || key == Keys.None) return;
        if (!_followers.TryGetValue(followerName.Trim(), out var follower))
        {
            LogInfo($"Hotkey ignored: follower '{followerName.Trim()}' is not connected. " +
                    $"Connected followers: {FormatConnectedFollowers()}.");
            return;
        }

        LogInfo($"Sending {key} to follower '{follower.Name}'.");
        _ = SendToFollowerAsync(follower, new Message { Type = "key", Key = (int)key });
    }

    private async Task SendToFollowerAsync(FollowerConnection follower, Message message)
    {
        try
        {
            await follower.SendAsync(message, _cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            RemoveFollower(follower);
        }
    }

    private async Task RunServerAsync()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            LogInfo($"Leader server is listening on 0.0.0.0:{_port}.");

            while (!_cancellation.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                LogInfo($"Accepted connection from {client.Client.RemoteEndPoint}.");
                _ = HandleFollowerAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            LogError($"Leader listener failed on port {_port}: {ex.Message}");
        }
    }

    private async Task HandleFollowerAsync(TcpClient client)
    {
        FollowerConnection connection = null;
        try
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream);
            var writer = new StreamWriter(stream) { AutoFlush = true };
            connection = new FollowerConnection(client, writer);

            while (!_cancellation.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                if (line == null) break;

                var message = JsonConvert.DeserializeObject<Message>(line);
                if (message?.Type != "register" || string.IsNullOrWhiteSpace(message.FollowerName)) continue;

                var name = message.FollowerName.Trim();
                connection.Name = name;
                if (_followers.TryGetValue(name, out var oldConnection) && oldConnection != connection)
                {
                    oldConnection.Dispose();
                }
                _followers[name] = connection;
                LogInfo($"Follower '{name}' registered. Connected followers: {FormatConnectedFollowers()}.");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex)
        {
            LogError($"Follower connection failed: {ex.Message}");
        }
        finally
        {
            if (connection != null) RemoveFollower(connection);
            else client.Dispose();
        }
    }

    private async Task RunClientAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                await client.ConnectAsync(_leaderAddress, _port, _cancellation.Token).ConfigureAwait(false);
                LogInfo($"Connected to leader at {_leaderAddress}:{_port}.");
                _lastConnectionIssue = string.Empty;
                var stream = client.GetStream();
                var reader = new StreamReader(stream);
                var writer = new StreamWriter(stream) { AutoFlush = true };
                lock (_clientWriterLock) _clientWriter = writer;
                SendRegistration();

                while (!_cancellation.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                    if (line == null) break;

                    var message = JsonConvert.DeserializeObject<Message>(line);
                    // Keys is a flags enum, so valid combinations (for example Ctrl+A) are not
                    // necessarily named enum members and must not be rejected by Enum.IsDefined.
                    if (message?.Type == "key" && message.Key > 0)
                    {
                        _receivedKeys.Enqueue((Keys)message.Key);
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
                lock (_clientWriterLock) _clientWriter = null;
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
        if (string.IsNullOrWhiteSpace(_followerName)) return;

        StreamWriter writer;
        lock (_clientWriterLock) writer = _clientWriter;
        if (writer == null) return;

        _ = WriteRegistrationAsync(writer, _followerName);
    }

    private async Task WriteRegistrationAsync(StreamWriter writer, string followerName)
    {
        try
        {
            await writer.WriteLineAsync(JsonConvert.SerializeObject(new Message
            {
                Type = "register",
                FollowerName = followerName,
            })).ConfigureAwait(false);
            LogInfo($"Registered with leader as follower '{followerName}'.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void RemoveFollower(FollowerConnection follower)
    {
        if (!string.IsNullOrWhiteSpace(follower.Name))
        {
            ((ICollection<KeyValuePair<string, FollowerConnection>>)_followers)
                .Remove(new KeyValuePair<string, FollowerConnection>(follower.Name, follower));
        }
        follower.Dispose();
    }

    private string FormatConnectedFollowers()
    {
        var names = _followers.Keys;
        return names.Count == 0 ? "none" : string.Join(", ", names);
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
        _listener?.Stop();

        lock (_clientWriterLock)
        {
            _clientWriter?.Dispose();
            _clientWriter = null;
        }

        foreach (var follower in _followers.Values) follower.Dispose();
        _followers.Clear();
        _cancellation.Dispose();
    }
}
