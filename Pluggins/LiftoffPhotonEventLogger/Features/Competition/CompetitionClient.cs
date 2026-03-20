using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using LiftoffPhotonEventLogger.Features.MultiplayerTrackControl;
using SyncContext = System.Threading.SynchronizationContext;

namespace LiftoffPhotonEventLogger.Features.Competition;

/// <summary>
/// Maintains a persistent WebSocket connection to the competition server.
///
/// Threading model:
///   - All WebSocket I/O runs on a background Task.
///   - Events to send are enqueued from the Unity main thread via EnqueueEvent().
///   - Commands received from the server are queued and dispatched on the Unity
///     main thread inside Update().
/// </summary>
internal sealed class CompetitionClient : IDisposable
{
    private readonly CompetitionConfig _config;
    private readonly ManualLogSource _log;
    private readonly MultiplayerTrackControlService _trackControl;
    private readonly SyncContext? _unitySyncContext;

    private const int MaxOutboxSize = 10_000;

    private readonly ConcurrentQueue<string> _outbox = new();
    private readonly SemaphoreSlim _outboxSignal = new(0);
    private readonly Action<Dictionary<string, object?>>? _onCatalogReady;
    private readonly Action<int>? _onKickPlayer;
    private readonly Action? _onTrackChanging;
    private readonly Action? _onConnected;

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private bool _disposed;

    public CompetitionClient(
        CompetitionConfig config,
        ManualLogSource log,
        MultiplayerTrackControlService trackControl,
        SyncContext? unitySyncContext,
        Action<Dictionary<string, object?>>? onCatalogReady = null,
        Action<int>? onKickPlayer = null,
        Action? onTrackChanging = null,
        Action? onConnected = null)
    {
        _config = config;
        _log = log;
        _trackControl = trackControl;
        _unitySyncContext = unitySyncContext;
        _onCatalogReady = onCatalogReady;
        _onKickPlayer = onKickPlayer;
        _onTrackChanging = onTrackChanging;
        _onConnected = onConnected;
    }

    public void Start()
    {
        if (!_config.Enabled.Value)
        {
            _log.LogInfo("[Competition] Disabled — set Competition.Enabled = true in config to connect.");
            return;
        }

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
        _log.LogInfo($"[Competition] Client started. Target: {_config.ServerUrl.Value}");
    }

    /// <summary>No longer needed — kept for compatibility. Commands now use SynchronizationContext.</summary>
    public void Update() { }

    /// <summary>Forward a JSONL event line to the server. Safe to call from any thread.</summary>
    public void EnqueueEvent(string jsonLine)
    {
        if (_config.Enabled.Value && _outbox.Count < MaxOutboxSize)
        {
            _outbox.Enqueue(jsonLine);
            _outboxSignal.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _workerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutdown timeout */ }
        _cts?.Dispose();
        _cts = null;
        _outboxSignal.Dispose();
    }

    // ── Background worker ─────────────────────────────────────────────────

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Competition] Disconnected: {ex.Message}. Reconnecting in {_config.ReconnectDelaySecs.Value}s...");
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(_config.ReconnectDelaySecs.Value), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        _log.LogInfo("[Competition] Worker stopped.");
    }

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey.Value}");

        var uri = new Uri(_config.ServerUrl.Value);
        _log.LogInfo($"[Competition] Connecting to {uri}...");

        await ws.ConnectAsync(uri, ct);
        _log.LogInfo("[Competition] Connected.");
        RunOnMainThread(() => _onConnected?.Invoke());

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var sendTask    = SendLoopAsync(ws, linkedCts.Token);
        var receiveTask = ReceiveLoopAsync(ws, linkedCts.Token);

        // If either loop exits, cancel the other and exit so we reconnect
        await Task.WhenAny(sendTask, receiveTask);
        linkedCts.Cancel();

        try { await Task.WhenAll(sendTask, receiveTask); } catch { /* swallow */ }

        _log.LogInfo("[Competition] Connection closed.");
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // Wait for an item to be enqueued instead of polling every 50ms
            await _outboxSignal.WaitAsync(ct);

            // Drain all queued items
            while (_outbox.TryDequeue(out var line))
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuffer = new StringBuilder();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Acknowledged", ct);
                return;
            }

            messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                var text = messageBuffer.ToString().Trim();
                messageBuffer.Clear();
                if (text.Length > 0)
                    DispatchCommand(text);
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void RunOnMainThread(Action action)
    {
        if (_disposed) return;
        if (_unitySyncContext != null)
        {
            _unitySyncContext.Post(_ => action(), null);
        }
        else
        {
            _log.LogWarning("[Competition] No SyncContext — running command inline on background thread (Unity APIs may fail).");
            action();
        }
    }

    // ── Command acknowledgment ──────────────────────────────────────────────

    private void EmitCommandAck(string? commandId, string status, string message = "")
    {
        if (string.IsNullOrEmpty(commandId)) return;
        var ack = $"{{\"event_type\":\"command_ack\",\"command_id\":\"{commandId}\",\"status\":\"{status}\",\"message\":\"{message}\"}}";
        EnqueueEvent(ack);
    }

    // ── Command dispatch ───────────────────────────────────────────────────

    private void DispatchCommand(string json)
    {
        var fields = SimpleJsonParser.TryParseObject(json);
        if (fields == null || !fields.TryGetValue("cmd", out var cmd))
        {
            _log.LogWarning($"[Competition] Received unrecognised message: {json.Substring(0, Math.Min(120, json.Length))}");
            return;
        }

        fields.TryGetValue("command_id", out var commandId);

        switch (cmd)
        {
            case "next_track":
                _log.LogInfo("[Competition] next_track received — scheduling on main thread.");
                RunOnMainThread(() =>
                {
                    try
                    {
                        _onTrackChanging?.Invoke();
                        _trackControl.ExternalCycleNext("competition-server");
                        EmitCommandAck(commandId, "ok");
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] next_track failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "set_track":
                fields.TryGetValue("env",        out var env);
                fields.TryGetValue("track",       out var track);
                fields.TryGetValue("race",        out var race);
                fields.TryGetValue("workshop_id", out var workshopId);
                _log.LogInfo($"[Competition] set_track received — env={env} track={track} race={race}");
                RunOnMainThread(() =>
                {
                    try
                    {
                        _onTrackChanging?.Invoke();
                        _trackControl.ExternalSetTrack(env ?? "", track ?? "", race ?? "", workshopId ?? "", "competition-server");
                        EmitCommandAck(commandId, "ok");
                    }
                    catch (Exception ex) { _log.LogWarning($"[Competition] set_track failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                });
                break;

            case "update_playlist":
                fields.TryGetValue("sequence", out var sequence);
                var applyImmediately = fields.TryGetValue("apply_immediately", out var applyVal)
                    && string.Equals(applyVal, "true", StringComparison.OrdinalIgnoreCase);
                _log.LogInfo($"[Competition] update_playlist received — applyImmediately={applyImmediately}");
                if (!string.IsNullOrWhiteSpace(sequence))
                {
                    var seq = sequence!;
                    RunOnMainThread(() =>
                    {
                        try { _trackControl.ExternalUpdatePlaylist(seq, applyImmediately, "competition-server"); EmitCommandAck(commandId, "ok"); }
                        catch (Exception ex) { _log.LogWarning($"[Competition] update_playlist failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                    });
                }
                break;

            case "request_catalog":
                _log.LogInfo("[Competition] request_catalog received — scheduling on main thread.");
                RunOnMainThread(() =>
                {
                    try
                    {
                        if (_onCatalogReady == null)
                        {
                            _log.LogWarning("[Competition] request_catalog: no catalog callback set.");
                            EmitCommandAck(commandId, "error", "no catalog callback");
                        }
                        else if (_trackControl.ExternalTryCatalogSnapshot(out var catalog))
                        {
                            catalog.TryGetValue("environments", out var envList);
                            _log.LogInfo($"[Competition] Catalog captured: {(envList as System.Collections.IList)?.Count ?? 0} environments.");
                            _onCatalogReady(catalog);
                            EmitCommandAck(commandId, "ok");
                        }
                        else
                        {
                            _log.LogWarning("[Competition] request_catalog: popup not available — open the track selection popup in-game first.");
                            EmitCommandAck(commandId, "error", "popup not available");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"[Competition] request_catalog failed: {ex.GetType().Name}: {ex.Message}");
                        EmitCommandAck(commandId, "error", ex.Message);
                    }
                });
                break;

            case "send_chat":
                fields.TryGetValue("message", out var chatMessage);
                if (!string.IsNullOrWhiteSpace(chatMessage))
                {
                    var msg = chatMessage!;
                    _log.LogInfo($"[Competition] send_chat received: \"{msg}\"");
                    RunOnMainThread(() =>
                    {
                        try { _trackControl.ExternalSendChat(msg, "competition-server"); EmitCommandAck(commandId, "ok"); }
                        catch (Exception ex) { _log.LogWarning($"[Competition] send_chat failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                    });
                }
                break;

            case "kick_player":
                if (fields.TryGetValue("actor", out var actorStr) && int.TryParse(actorStr, out var actorNum))
                {
                    _log.LogInfo($"[Competition] kick_player received — actor={actorNum}");
                    var a = actorNum;
                    RunOnMainThread(() =>
                    {
                        try { _onKickPlayer?.Invoke(a); EmitCommandAck(commandId, "ok"); }
                        catch (Exception ex) { _log.LogWarning($"[Competition] kick_player failed: {ex.GetType().Name}: {ex.Message}"); EmitCommandAck(commandId, "error", ex.Message); }
                    });
                }
                break;

            default:
                _log.LogWarning($"[Competition] Unknown command: {cmd}");
                break;
        }
    }
}
