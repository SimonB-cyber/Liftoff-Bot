using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using Photon.Realtime;
using UnityEngine;

namespace LiftoffPhotonEventLogger.Features.MultiplayerTrackControl;

internal sealed class MultiplayerTrackControlService : IDisposable
{
    private readonly BaseUnityPlugin _plugin;
    private readonly string _pluginDir;
    private readonly Func<object?, string> _describe;
    private readonly MultiplayerTrackControlConfig _config;
    private readonly MultiplayerTrackControlLog _log;
    private readonly MultiplayerDiscoveryService _discovery;
    private readonly MultiplayerHostStateDetector _hostDetector;
    private readonly MultiplayerTrackChangeExecutor _executor;
    private readonly MultiplayerChatService _chatService;
    private readonly MultiplayerDiagnosticsPatches _patches;
    private static readonly TimeSpan PendingRetryDelay = TimeSpan.FromMilliseconds(500);
    private const int MaxPendingRetryAttempts = 12;

    private MultiplayerHostStateDetector.HostStateSnapshot? _lastSnapshot;
    private Rect _debugWindowRect = new(20f, 20f, 430f, 230f);
    private DateTime _lastPollUtc = DateTime.MinValue;
    private bool _hasLoggedTickSource;
    private bool _initialized;
    private bool _wasInMultiplayer;
    private bool _wasHost;
    private readonly List<PendingExecutorAction> _pendingExecutorActions = new();
    private readonly HashSet<string> _activeConfigCommandKeys = new(StringComparer.OrdinalIgnoreCase);

    public MultiplayerTrackControlService(
        BaseUnityPlugin plugin,
        string pluginDir,
        ManualLogSource logger,
        Action<string> stateLog,
        Func<object?, string> describe)
    {
        _plugin = plugin;
        _pluginDir = pluginDir;
        _describe = describe;
        _config = new MultiplayerTrackControlConfig(plugin.Config);
        _log = new MultiplayerTrackControlLog(logger, stateLog);
        _discovery = new MultiplayerDiscoveryService(_log, describe);
        _hostDetector = new MultiplayerHostStateDetector(_discovery, _log, describe);
        _executor = new MultiplayerTrackChangeExecutor(_config, _discovery, _hostDetector, _log, describe);
        _chatService = new MultiplayerChatService(_config, _discovery, _log, describe);
        _patches = new MultiplayerDiagnosticsPatches($"{Plugin.PluginGuid}.multiplayertrackcontrol", _discovery, _log, describe);
    }

    public void Initialize()
    {
        if (_initialized || !_config.EnableMultiplayerTrackControl.Value)
            return;

        _log.Info("INIT", $"Build marker={Plugin.BuildMarker}");
        _log.Info("INIT", $"Experimental multiplayer track control enabled. pluginDir={_pluginDir}");
        _log.Info("INIT", $"Hotkeys discovery={FormatShortcut(_config.TriggerDiscoveryHotkey.Value)} state={FormatShortcut(_config.TriggerStateDumpHotkey.Value)} change={FormatShortcut(_config.TriggerTrackChangeHotkey.Value)} cycle={FormatShortcut(_config.TriggerCycleNextHotkey.Value)}");
        _log.Info("INIT", $"Fallbacks rawInput={_config.EnableRawHotkeyFallback.Value} debugPanel={_config.ShowDebugPanel.Value} delayExecutorCommandClear={_config.DelayExecutorCommandClearUntilCompletion.Value} chatDiagnostics={_config.EnableChatDiagnostics.Value}");
        _log.Info("INIT", $"Targets env=\"{_config.TargetEnvironmentName.Value}\" track=\"{_config.TargetTrackName.Value}\" race=\"{_config.TargetRaceName.Value}\" workshop=\"{_config.TargetWorkshopId.Value}\"");
        _log.Info("INIT", $"Sequence loop={_config.LoopTargetSequence.Value} raw=\"{_config.TargetSequence.Value}\"");
        _discovery.Refresh();

        if (_config.EnableHarmonyDiagnostics.Value)
            _patches.Install();

        _initialized = true;
    }

    public void Update()
    {
        Poll("Update", force: false);
    }

    public void NotifyActivity(string source)
    {
        Poll(source, force: true);
    }

    // ── External API (called by CompetitionClient on the Unity main thread) ──

    public void ExternalCycleNext(string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External cycle-next requested from {source}");
        RunExecutorAction("cycle-next track/race", _executor.AttemptCycleNext, source);
    }

    public void ExternalSetTrack(string env, string track, string race, string workshopId, string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External set-track requested from {source}: env=\"{env}\" track=\"{track}\" race=\"{race}\" workshop=\"{workshopId}\"");
        _config.TargetEnvironmentName.Value = env;
        _config.TargetTrackName.Value = track;
        _config.TargetRaceName.Value = race;
        _config.TargetWorkshopId.Value = workshopId;
        RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, source);
    }

    public bool ExternalTryCatalogSnapshot(out Dictionary<string, object?> catalog)
    {
        catalog = new Dictionary<string, object?>();
        if (!_initialized) return false;
        return _executor.TryCatalogSnapshot(out catalog);
    }

    public void ExternalUpdatePlaylist(string sequence, bool applyImmediately, string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External update-playlist requested from {source}: applyImmediately={applyImmediately} sequence=\"{sequence}\"");
        _config.TargetSequence.Value = sequence;
        if (applyImmediately)
            RunExecutorAction("cycle-next track/race", _executor.AttemptCycleNext, source);
    }

    public void ExternalSendChat(string message, string source)
    {
        if (!_initialized) return;
        _log.Info("API", $"External send-chat requested from {source}: \"{message}\"");
        _chatService.SendRaw(message);
    }

    private void Poll(string source, bool force)
    {
        if (!_initialized)
            return;

        try
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastPollUtc < TimeSpan.FromMilliseconds(250))
                return;

            _lastPollUtc = now;
            if (!_hasLoggedTickSource)
            {
                _hasLoggedTickSource = true;
                _log.Info("TICK", $"First multiplayer track control poll observed via {source}");
            }

            var snapshot = _hostDetector.Capture();
            _lastSnapshot = snapshot;
            if (snapshot.IsInMultiplayer != _wasInMultiplayer)
            {
                _wasInMultiplayer = snapshot.IsInMultiplayer;
                _log.Info("HOST", $"Multiplayer state changed: inMultiplayer={snapshot.IsInMultiplayer} reason={snapshot.InMultiplayerReason}");
                if (snapshot.IsInMultiplayer && _config.AutoDumpOnLobbyJoin.Value)
                {
                    _discovery.DumpDiscovery();
                    _executor.DumpCurrentState();
                    if (_config.EnableChatDiagnostics.Value)
                        _chatService.DumpChatState();
                }
            }

            if (snapshot.IsHost != _wasHost)
            {
                _wasHost = snapshot.IsHost;
                _log.Info("HOST", $"Host state changed: isHost={snapshot.IsHost} reason={snapshot.HostReason}");
            }

            if ((!snapshot.IsInMultiplayer || !snapshot.IsHost) && _pendingExecutorActions.Count > 0)
            {
                CancelPendingExecutorActions($"multiplayer/host state changed. inMultiplayer={snapshot.IsInMultiplayer} isHost={snapshot.IsHost}");
            }

            RetryPendingExecutorActions(now);

            if (ShouldRunAction(_config.TriggerDiscoveryHotkey.Value, _config.RequestDiscoveryDump, "discovery dump"))
            {
                _discovery.DumpDiscovery();
            }

            if (ShouldRunAction(_config.TriggerStateDumpHotkey.Value, _config.RequestStateDump, "state dump"))
            {
                _executor.DumpCurrentState();
            }

            if (TryConsumeConfigCommand(_config.RequestDumpChatState, "chat state dump", allowDelayedClear: false))
            {
                _chatService.DumpChatState();
            }

            if (TryConsumeConfigCommand(_config.RequestSendChatMessage, "chat send", allowDelayedClear: false))
            {
                _chatService.SendConfiguredMessage();
            }

            if (TryConsumeConfigCommand(_config.RequestAdvanceSequence, "advance target sequence", allowDelayedClear: false))
            {
                QueueNextSequenceTarget();
            }

            if (TryConsumeShortcut(_config.TriggerTrackChangeHotkey.Value, "configured track/race change"))
            {
                RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, "hotkey");
            }
            else if (TryConsumeConfigCommand(_config.RequestTrackChange, "configured track/race change", allowDelayedClear: true))
            {
                RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, "config", _config.RequestTrackChange);
            }

            if (TryConsumeShortcut(_config.TriggerCycleNextHotkey.Value, "cycle-next track/race"))
            {
                RunExecutorAction("cycle-next track/race", _executor.AttemptCycleNext, "hotkey");
            }
            else if (TryConsumeConfigCommand(_config.RequestCycleNext, "cycle-next track/race", allowDelayedClear: true))
            {
                RunExecutorAction("cycle-next track/race", _executor.AttemptCycleNext, "config", _config.RequestCycleNext);
            }
        }
        catch (Exception ex)
        {
            _log.Error("UPDATE", "Multiplayer track control update loop failed.", ex);
        }
    }

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", $"Player entered room: actor={newPlayer.ActorNumber} nick=\"{newPlayer.NickName}\"");
    }

    public void OnPlayerLeftRoom(Player otherPlayer)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", $"Player left room: actor={otherPlayer.ActorNumber} nick=\"{otherPlayer.NickName}\"");
    }

    public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", $"Room properties changed: {ReflectionHelper.SafeDescribe(_describe, propertiesThatChanged)}");
    }

    public void OnPlayerPropertiesUpdate(Player player, Hashtable changedProps)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", $"Player properties changed: actor={player.ActorNumber} nick=\"{player.NickName}\" props={ReflectionHelper.SafeDescribe(_describe, changedProps)}");
    }

    public void OnMasterClientSwitched(Player newMasterClient)
    {
        if (!_initialized)
            return;

        _log.Info("PHOTON", $"Master client switched: actor={newMasterClient.ActorNumber} nick=\"{newMasterClient.NickName}\"");
        _executor.DumpCurrentState();
    }

    public void Dispose()
    {
        _patches.Dispose();
    }

    public void OnGUI()
    {
        if (!_initialized || !_config.ShowDebugPanel.Value)
            return;

        _debugWindowRect = GUILayout.Window(
            GetHashCode(),
            _debugWindowRect,
            DrawDebugWindow,
            "MTC Debug");
    }

    private void DrawDebugWindow(int windowId)
    {
        var snapshot = _lastSnapshot ?? _hostDetector.Capture();

        GUILayout.Label($"In multiplayer: {snapshot.IsInMultiplayer} ({snapshot.InMultiplayerReason})");
        GUILayout.Label($"Is host: {snapshot.IsHost} ({snapshot.HostReason})");
        GUILayout.Label($"Dry run: {_config.EnableDryRun.Value}");
        GUILayout.Label($"Target race: {_config.TargetRaceName.Value}");
        GUILayout.Label($"Target track: {_config.TargetTrackName.Value}");
        GUILayout.Label($"Target env: {_config.TargetEnvironmentName.Value}");

        if (GUILayout.Button("Dump Discovery"))
            RunUiAction("Discovery dump requested from debug panel.", () => _discovery.DumpDiscovery());

        if (GUILayout.Button("Dump State"))
            RunUiAction("State dump requested from debug panel.", _executor.DumpCurrentState);

        if (GUILayout.Button("Dump Chat"))
            RunUiAction("Chat state dump requested from debug panel.", _chatService.DumpChatState);

        if (GUILayout.Button("Attempt Configured Change"))
            RunUiExecutorAction("Configured track/race change requested from debug panel.", "configured track/race change", _executor.AttemptConfiguredChange);

        if (GUILayout.Button("Cycle Next"))
            RunUiExecutorAction("Cycle-next requested from debug panel.", "cycle-next track/race", _executor.AttemptCycleNext);

        if (GUILayout.Button("Advance Sequence"))
            RunUiAction("Advance target sequence requested from debug panel.", QueueNextSequenceTarget);

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }

    private void RunUiAction(string message, Action action)
    {
        try
        {
            _log.Info("UI", message);
            action();
        }
        catch (Exception ex)
        {
            _log.Error("UI", $"Debug panel action failed: {message}", ex);
        }
    }

    private void RunUiExecutorAction(string message, string actionName, Func<MultiplayerTrackChangeExecutionStatus> action)
    {
        try
        {
            _log.Info("UI", message);
            RunExecutorAction(actionName, action, "debug-panel");
        }
        catch (Exception ex)
        {
            _log.Error("UI", $"Debug panel executor action failed: {message}", ex);
        }
    }

    private bool ShouldRunAction(KeyboardShortcut hotkey, ConfigEntry<bool> commandEntry, string actionName)
    {
        if (TryConsumeShortcut(hotkey, actionName))
            return true;

        if (TryConsumeConfigCommand(commandEntry, actionName, allowDelayedClear: false))
            return true;

        return false;
    }

    private bool TryConsumeShortcut(KeyboardShortcut shortcut, string actionName)
    {
        if (shortcut.MainKey == KeyCode.None)
            return false;

        if (shortcut.IsDown())
        {
            _log.Info("HOTKEY", $"{actionName} requested via KeyboardShortcut {FormatShortcut(shortcut)}");
            return true;
        }

        if (!_config.EnableRawHotkeyFallback.Value)
            return false;

        if (!TryConsumeRawShortcut(shortcut, out var detail))
            return false;

        _log.Info("HOTKEY", $"{actionName} requested via raw input {detail}");
        return true;
    }

    private bool TryConsumeRawShortcut(KeyboardShortcut shortcut, out string detail)
    {
        detail = string.Empty;
        if (!Input.GetKeyDown(shortcut.MainKey))
            return false;

        var modifiers = shortcut.Modifiers.ToArray();
        if (modifiers.Any(modifier => !Input.GetKey(modifier)))
            return false;

        detail = $"{shortcut.MainKey} modifiers=[{string.Join(", ", modifiers.Select(modifier => modifier.ToString()))}]";
        return true;
    }

    private bool TryConsumeConfigCommand(ConfigEntry<bool> commandEntry, string actionName, bool allowDelayedClear)
    {
        if (!commandEntry.Value)
            return false;

        var commandKey = GetCommandKey(commandEntry);
        if (allowDelayedClear && _activeConfigCommandKeys.Contains(commandKey))
            return false;

        _log.Info("COMMAND", $"{actionName} requested via config entry {commandEntry.Definition.Section}.{commandEntry.Definition.Key}");
        if (allowDelayedClear && _config.DelayExecutorCommandClearUntilCompletion.Value)
        {
            _activeConfigCommandKeys.Add(commandKey);
        }
        else
        {
            ClearConfigCommandEntry(commandEntry, $"consumed for {actionName}");
        }

        return true;
    }

    private void RunExecutorAction(
        string actionName,
        Func<MultiplayerTrackChangeExecutionStatus> action,
        string sourceDescription,
        ConfigEntry<bool>? commandEntry = null)
    {
        var existingPending = FindPendingAction(actionName);
        if (existingPending != null)
        {
            existingPending.NextAttemptUtc = DateTime.UtcNow;
            _log.Info("EXEC", $"Action \"{actionName}\" is already queued. source={sourceDescription} nextAttempt={existingPending.NextAttemptUtc:O}");
            return;
        }

        var status = action();
        HandleExecutorStatus(new PendingExecutorAction(actionName, action, commandEntry, sourceDescription) { AttemptsStarted = 1 }, status, isRetry: false);
    }

    private void RetryPendingExecutorActions(DateTime now)
    {
        var dueActions = _pendingExecutorActions
            .Where(pending => now >= pending.NextAttemptUtc)
            .OrderBy(pending => pending.NextAttemptUtc)
            .ToList();

        foreach (var pendingAction in dueActions)
        {
            if (pendingAction.AttemptsStarted >= MaxPendingRetryAttempts)
            {
                _log.Warn("EXEC", $"Giving up on pending action \"{pendingAction.Name}\" after {pendingAction.AttemptsStarted} attempts without a live popup.");
                FinalizeExecutorAction(pendingAction, MultiplayerTrackChangeExecutionStatus.Failed, "Timed out waiting for a live popup/controller.");
                continue;
            }

            pendingAction.AttemptsStarted++;
            _log.Info("EXEC", $"Retrying deferred action \"{pendingAction.Name}\" attempt {pendingAction.AttemptsStarted}/{MaxPendingRetryAttempts}.");
            var status = pendingAction.Action();
            HandleExecutorStatus(pendingAction, status, isRetry: true);
        }
    }

    private void HandleExecutorStatus(PendingExecutorAction pendingAction, MultiplayerTrackChangeExecutionStatus status, bool isRetry)
    {
        if (status == MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup)
        {
            SchedulePendingExecutorAction(pendingAction, isRetry);
            return;
        }

        FinalizeExecutorAction(pendingAction, status, detail: null);
    }

    private void SchedulePendingExecutorAction(PendingExecutorAction pendingAction, bool isRetry)
    {
        var now = DateTime.UtcNow;
        var existingPending = FindPendingAction(pendingAction.Name);
        if (existingPending == null)
        {
            pendingAction.AttemptsStarted = Math.Max(pendingAction.AttemptsStarted, 1);
            pendingAction.NextAttemptUtc = now + PendingRetryDelay;
            _pendingExecutorActions.Add(pendingAction);
            LogExecutorSummary("EXEC", pendingAction, MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup, $"Queued until a live popup/controller becomes available. nextAttempt={pendingAction.NextAttemptUtc:O}");
            return;
        }

        if (!isRetry)
        {
            LogExecutorSummary("EXEC", existingPending, MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup, $"Already queued. nextAttempt={existingPending.NextAttemptUtc:O}");
            return;
        }

        existingPending.NextAttemptUtc = now + PendingRetryDelay;
        LogExecutorSummary("EXEC", existingPending, MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup, $"Still waiting for a live popup/controller. nextAttempt={existingPending.NextAttemptUtc:O}");
    }

    private void FinalizeExecutorAction(PendingExecutorAction pendingAction, MultiplayerTrackChangeExecutionStatus status, string? detail)
    {
        RemovePendingAction(pendingAction);
        CompleteExecutorConfigCommandIfNeeded(pendingAction, status);

        var category = status switch
        {
            MultiplayerTrackChangeExecutionStatus.Applied => "EXEC",
            MultiplayerTrackChangeExecutionStatus.DryRun => "EXEC",
            _ => "EXEC"
        };

        LogExecutorSummary(category, pendingAction, status, detail);

        if (status == MultiplayerTrackChangeExecutionStatus.Applied &&
            pendingAction.Name == "configured track/race change" &&
            _config.AnnounceTrackChangesInChat.Value)
        {
            _chatService.SendAnnouncement(
                _config.TargetEnvironmentName.Value,
                _config.TargetTrackName.Value,
                _config.TargetRaceName.Value,
                _config.TargetWorkshopId.Value);
        }
    }

    private void CancelPendingExecutorActions(string reason)
    {
        var pendingActions = _pendingExecutorActions.ToList();
        foreach (var pendingAction in pendingActions)
        {
            RemovePendingAction(pendingAction);
            CompleteExecutorConfigCommandIfNeeded(pendingAction, MultiplayerTrackChangeExecutionStatus.Failed);
            LogExecutorSummary("EXEC", pendingAction, MultiplayerTrackChangeExecutionStatus.Failed, $"Cancelled because {reason}");
        }
    }

    private void CompleteExecutorConfigCommandIfNeeded(PendingExecutorAction pendingAction, MultiplayerTrackChangeExecutionStatus status)
    {
        if (pendingAction.CommandEntry == null)
            return;

        var commandKey = pendingAction.CommandKey;
        if (!string.IsNullOrWhiteSpace(commandKey))
            _activeConfigCommandKeys.Remove(commandKey);

        if (_config.DelayExecutorCommandClearUntilCompletion.Value && pendingAction.CommandEntry.Value)
            ClearConfigCommandEntry(pendingAction.CommandEntry, $"terminal executor status={status}");
    }

    private void ClearConfigCommandEntry(ConfigEntry<bool> commandEntry, string reason)
    {
        if (!commandEntry.Value)
            return;

        commandEntry.Value = false;
        _plugin.Config.Save();
        _log.Info("COMMAND", $"Cleared config entry {commandEntry.Definition.Section}.{commandEntry.Definition.Key} ({reason}).");
    }

    private static string GetCommandKey(ConfigEntry<bool> commandEntry)
    {
        return $"{commandEntry.Definition.Section}.{commandEntry.Definition.Key}";
    }

    private PendingExecutorAction? FindPendingAction(string actionName)
    {
        return _pendingExecutorActions.FirstOrDefault(pending => string.Equals(pending.Name, actionName, StringComparison.OrdinalIgnoreCase));
    }

    private void RemovePendingAction(PendingExecutorAction pendingAction)
    {
        _pendingExecutorActions.RemoveAll(existing => string.Equals(existing.Name, pendingAction.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void LogExecutorSummary(string category, PendingExecutorAction pendingAction, MultiplayerTrackChangeExecutionStatus status, string? detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail={detail}";
        var message = $"Action \"{pendingAction.Name}\" status={status} attempts={pendingAction.AttemptsStarted}/{MaxPendingRetryAttempts} source={pendingAction.SourceDescription}{suffix}";

        switch (status)
        {
            case MultiplayerTrackChangeExecutionStatus.Applied:
            case MultiplayerTrackChangeExecutionStatus.DryRun:
                _log.Info(category, message);
                break;
            case MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup:
                _log.Info(category, message);
                break;
            default:
                _log.Warn(category, message);
                break;
        }
    }

    private void QueueNextSequenceTarget()
    {
        var entries = MultiplayerTargetSequence.Parse(_config.TargetSequence.Value, _log);
        if (entries.Count == 0)
        {
            _log.Warn("SEQUENCE", "TargetSequence is empty or contained no valid entries.");
            return;
        }

        if (!TryReadCurrentRoomTargets(out var currentEnvironment, out var currentTrack, out var currentRace))
        {
            _log.Warn("SEQUENCE", "Could not read current room environment/track/race. Falling back to the first sequence entry.");
            ApplySequenceEntry(entries[0]);
            RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, "sequence");
            return;
        }

        _log.Info("SEQUENCE", $"Current room target before advance: env=\"{currentEnvironment}\" track=\"{currentTrack}\" race=\"{currentRace}\"");

        if (!MultiplayerTargetSequence.TrySelectNext(entries, currentEnvironment, currentTrack, currentRace, _config.LoopTargetSequence.Value, out var next))
        {
            _log.Warn("SEQUENCE", $"No next sequence entry was available. current={currentEnvironment}|{currentTrack}|{currentRace} loop={_config.LoopTargetSequence.Value}");
            return;
        }

        ApplySequenceEntry(next);
        RunExecutorAction("configured track/race change", _executor.AttemptConfiguredChange, "sequence");
    }

    private bool TryReadCurrentRoomTargets(out string environmentName, out string trackName, out string raceName)
    {
        environmentName = string.Empty;
        trackName = string.Empty;
        raceName = string.Empty;

        var room = Photon.Pun.PhotonNetwork.CurrentRoom;
        if (room?.CustomProperties == null)
            return false;

        if (room.CustomProperties.TryGetValue("E", out var environment))
            environmentName = environment as string ?? string.Empty;

        if (room.CustomProperties.TryGetValue("T", out var track))
            trackName = ReflectionHelper.GetMemberValue(track, "Name") as string ?? track?.ToString() ?? string.Empty;

        if (room.CustomProperties.TryGetValue("R", out var race))
            raceName = ReflectionHelper.GetMemberValue(race, "Name") as string ?? race?.ToString() ?? string.Empty;

        return !string.IsNullOrWhiteSpace(environmentName) ||
               !string.IsNullOrWhiteSpace(trackName) ||
               !string.IsNullOrWhiteSpace(raceName);
    }

    private void ApplySequenceEntry(MultiplayerTargetSequence.SequenceEntry entry)
    {
        _config.TargetEnvironmentName.Value = entry.EnvironmentName;
        _config.TargetTrackName.Value = entry.TrackName;
        _config.TargetRaceName.Value = entry.RaceName;
        _config.TargetWorkshopId.Value = entry.WorkshopId;
        _plugin.Config.Save();

        _log.Info("SEQUENCE", $"Applied next sequence target: env=\"{entry.EnvironmentName}\" track=\"{entry.TrackName}\" race=\"{entry.RaceName}\" workshop=\"{entry.WorkshopId}\"");
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        var modifiers = shortcut.Modifiers.ToArray();
        if (modifiers.Length == 0)
            return shortcut.MainKey.ToString();

        return string.Join(" + ", modifiers.Select(modifier => modifier.ToString()).Concat(new[] { shortcut.MainKey.ToString() }));
    }

    private sealed class PendingExecutorAction
    {
        public PendingExecutorAction(
            string name,
            Func<MultiplayerTrackChangeExecutionStatus> action,
            ConfigEntry<bool>? commandEntry,
            string sourceDescription)
        {
            Name = name;
            Action = action;
            CommandEntry = commandEntry;
            SourceDescription = sourceDescription;
            CommandKey = commandEntry == null ? string.Empty : GetCommandKey(commandEntry);
        }

        public string Name { get; }
        public Func<MultiplayerTrackChangeExecutionStatus> Action { get; }
        public ConfigEntry<bool>? CommandEntry { get; }
        public string SourceDescription { get; }
        public string CommandKey { get; }
        public int AttemptsStarted { get; set; }
        public DateTime NextAttemptUtc { get; set; }
    }
}
