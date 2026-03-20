using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using LiftoffPhotonEventLogger.Features.Chat;
using LiftoffPhotonEventLogger.Features.Competition;
using LiftoffPhotonEventLogger.Features.Identity;
using LiftoffPhotonEventLogger.Features.Logging;
using LiftoffPhotonEventLogger.Features.MultiplayerTrackControl;
using LiftoffPhotonEventLogger.Features.Racing;

namespace LiftoffPhotonEventLogger;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin, IOnEventCallback, IInRoomCallbacks
{
    public const string PluginGuid = "uk.co.geekhost.liftoff.photoneventlogger";
    public const string PluginName = "Liftoff Photon Event Logger";
    public const string PluginVersion = "1.0.0";
    public const string BuildMarker = "build-20260318-game-rpc-kick-v2";

    private ManualLogSource _log = null!;
    private string _filePath = null!;
    private string _stateFilePath = null!;
    private string _raceFilePath = null!;
    private string _raceJsonFilePath = null!;
    private string _eventCodeDir = null!;
    private string _pluginDir = null!;
    private string _sessionId = string.Empty;
    private long _raceEventOrdinal;
    private bool _registered;
    private bool _isQuitting;
    private readonly PlayerIdentityStore _identity = new();
    private RaceStateProjector _raceState = null!;
    private MultiplayerTrackControlService? _multiplayerTrackControl;
    private CompetitionClient? _competitionClient;
    private ChatCaptureService? _chatCapture;
    private ConfigEntry<int> _trackChangeGraceSecs = null!;

    // Background log writer — drains queued file writes off the main thread
    private readonly BlockingCollection<(string path, string text)> _logQueue = new(1000);
    private System.Threading.Thread? _logWriterThread;

    protected void Awake()
    {
        _log = Logger;
        DontDestroyOnLoad(gameObject);

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll();

        _pluginDir = Path.Combine(Paths.BepInExRootPath, "plugins", "LiftoffPhotonEventLogger");
        Directory.CreateDirectory(_pluginDir);
        _eventCodeDir = Path.Combine(_pluginDir, "event-codes");
        Directory.CreateDirectory(_eventCodeDir);
        CleanOldLogs(_pluginDir, _eventCodeDir, maxAgeDays: 7);

        _filePath = Path.Combine(_pluginDir, $"photon-events-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _stateFilePath = Path.Combine(_pluginDir, $"photon-state-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _raceFilePath = Path.Combine(_pluginDir, $"photon-race-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _raceJsonFilePath = Path.Combine(_pluginDir, $"photon-race-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        _sessionId = Guid.NewGuid().ToString("N");
        _raceEventOrdinal = 0;
        File.WriteAllText(_filePath, $"[{DateTime.UtcNow:O}] {PluginName} session started{Environment.NewLine}");
        File.WriteAllText(_stateFilePath, $"[{DateTime.UtcNow:O}] {PluginName} state session started{Environment.NewLine}");
        File.WriteAllText(_raceFilePath, $"[{DateTime.UtcNow:O}] {PluginName} race session started{Environment.NewLine}");
        File.WriteAllText(_raceJsonFilePath, string.Empty);

        // Start background log writer thread so file I/O doesn't block the main thread
        _logWriterThread = new System.Threading.Thread(() =>
        {
            foreach (var (path, text) in _logQueue.GetConsumingEnumerable())
            {
                try { File.AppendAllText(path, text + Environment.NewLine); }
                catch { /* best effort */ }
            }
        }) { IsBackground = true, Name = "LiftoffLogWriter" };
        _logWriterThread.Start();

        _log.LogInfo($"{PluginName} loaded. Log file: {_filePath}");
        _log.LogInfo($"{PluginName} build marker: {BuildMarker}");
        AppendStateLine($"BUILD marker={BuildMarker} version={PluginVersion}");

        var minLapMs = Config.Bind(
            "Recording", "MinLapMs", 0,
            "Hard minimum lap time in milliseconds. Laps shorter than this are always discarded. " +
            "Set to 0 (default) to rely on smarter detection instead of a fixed floor.");

        _trackChangeGraceSecs = Config.Bind(
            "Recording", "TrackChangeGraceSeconds", 10,
            "Seconds after a track change command during which lap events are suppressed. " +
            "When the admin changes track, pilots mid-lap receive a partial-lap event from the game. " +
            "This window discards those bogus times. Increase if you see partial laps slipping through.");

        var maxLapsPerRace = Config.Bind(
            "Recording", "MaxLapsPerRace", 0,
            "Maximum laps per pilot per race before they are marked complete and further laps ignored. " +
            "Set to 0 (default) for InfiniteRace mode where laps accumulate without limit. " +
            "Set to 3 for ClassicRace mode.");

        var initialRaceId = Guid.NewGuid().ToString("N");
        _raceState = new RaceStateProjector(
            _identity, Logger, AppendRaceLine, AppendRaceEvent,
            minLapMs, maxLapsPerRace, initialRaceId, 1);

        AppendRaceEvent("session_started", new Dictionary<string, object?>
        {
            ["plugin"] = PluginName,
            ["version"] = PluginVersion,
            ["buildMarker"] = BuildMarker
        });

        _multiplayerTrackControl = new MultiplayerTrackControlService(
            this,
            _pluginDir,
            Logger,
            AppendStateLine,
            obj => ObjectDescriber.Describe(obj));
        _multiplayerTrackControl.Initialize();

        var competitionConfig = new CompetitionConfig(Config);
        var unitySyncContext = System.Threading.SynchronizationContext.Current;
        _competitionClient = new CompetitionClient(
            competitionConfig, Logger, _multiplayerTrackControl, unitySyncContext,
            onCatalogReady: catalogData => AppendRaceEvent("track_catalog", catalogData),
            onKickPlayer: actor => TryKickPlayer(actor),
            onTrackChanging: () =>
            {
                var graceSecs = _trackChangeGraceSecs.Value;
                _raceState.SuppressLapEventsUntil(DateTime.UtcNow.AddSeconds(graceSecs));
                _log.LogInfo($"[Recording] Track change — suppressing lap events for {graceSecs}s");
                _raceState.StartNewRace("track_change");
                EmitPlayerList();
            },
            onConnected: () =>
            {
                _competitionClient?.EnqueueEvent(
                    JsonSerializer.SerializeJsonObject(new Dictionary<string, object?>
                    {
                        ["event_type"]    = "session_started",
                        ["timestamp_utc"] = DateTime.UtcNow.ToString("O"),
                        ["session_id"]    = _sessionId,
                        ["race_id"]       = _raceState.RaceId,
                        ["race_ordinal"]  = _raceState.RaceOrdinal,
                        ["event_ordinal"] = _raceEventOrdinal,
                        ["plugin"]        = PluginName,
                        ["version"]       = PluginVersion,
                        ["buildMarker"]   = BuildMarker,
                    })
                );
                EmitPlayerList();
            });
        _competitionClient.Start();

        // Re-send session_started now that the client exists so the server
        // receives it and can create the session row before any race events.
        _competitionClient.EnqueueEvent(
            JsonSerializer.SerializeJsonObject(new Dictionary<string, object?>
            {
                ["event_type"]    = "session_started",
                ["timestamp_utc"] = DateTime.UtcNow.ToString("O"),
                ["session_id"]    = _sessionId,
                ["race_id"]       = _raceState.RaceId,
                ["race_ordinal"]  = _raceState.RaceOrdinal,
                ["event_ordinal"] = _raceEventOrdinal,
                ["plugin"]        = PluginName,
                ["version"]       = PluginVersion,
                ["buildMarker"]   = BuildMarker,
            })
        );

        _chatCapture = new ChatCaptureService(Logger, PluginGuid, (userId, userName, message) =>
        {
            int? actor = _identity.TryFindActorByUserId(userId, out var found) ? found : null;
            AppendRaceEvent("chat_message", new Dictionary<string, object?>
            {
                ["actor"]   = actor,
                ["user_id"] = userId,
                ["nick"]    = userName,
                ["message"] = message
            });
        });
        _chatCapture.Install();
    }


    protected void OnEnable()
    {
        // Try immediately (in case Photon is already initialised)
        TryRegister();
    }

    private int _updateCount;
    protected void Update()
    {
        _updateCount++;

        // Keep trying until it works – Liftoff may initialise Photon later
        if (!_registered)
            TryRegister();

        _multiplayerTrackControl?.Update();
        _competitionClient?.Update();
    }

    private void OnDisable()
    {
        // Keep callback active through scene/enable state changes.
    }

    protected void OnGUI()
    {
        _multiplayerTrackControl?.OnGUI();
    }

    protected void OnDestroy()
    {
        if (_isQuitting)
        {
            _chatCapture?.Dispose();
            _chatCapture = null;
            _competitionClient?.Dispose();
            _competitionClient = null;
            _multiplayerTrackControl?.Dispose();
            _multiplayerTrackControl = null;
            TryUnregister();
            return;
        }

        _log.LogWarning("Plugin OnDestroy fired before OnApplicationQuit. Callback unregistration skipped.");
    }

    protected void OnApplicationQuit()
    {
        _isQuitting = true;
        _chatCapture?.Dispose();
        _chatCapture = null;
        _competitionClient?.Dispose();
        _competitionClient = null;
        _multiplayerTrackControl?.Dispose();
        _multiplayerTrackControl = null;
        TryUnregister();

        // Drain remaining log writes and shut down the background writer
        _logQueue.CompleteAdding();
        _logWriterThread?.Join(TimeSpan.FromSeconds(2));
    }

    private void TryRegister()
    {
        if (_registered) return;

        try
        {
            PhotonNetwork.AddCallbackTarget(this);
            _registered = true;
            _log.LogInfo("Registered as Photon callback target (IOnEventCallback).");
            EmitPlayerList();
        }
        catch
        {
            // Photon not ready yet; ignore and retry next frame
        }
    }

    private void TryKickPlayer(int actor)
    {
        try
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                _log.LogWarning($"[Plugin] Cannot kick Actor={actor}: not master client");
                AppendRaceEvent("kick_result", new Dictionary<string, object?>
                {
                    ["actor"] = actor,
                    ["success"] = false,
                    ["reason"] = "not_master_client"
                });
                return;
            }

            var player = PhotonNetwork.CurrentRoom?.GetPlayer(actor);
            if (player == null)
            {
                _log.LogWarning($"[Plugin] Cannot kick Actor={actor}: player not found in room");
                AppendRaceEvent("kick_result", new Dictionary<string, object?>
                {
                    ["actor"] = actor,
                    ["success"] = false,
                    ["reason"] = "player_not_found"
                });
                return;
            }

            var nick = player.NickName;

            if (string.IsNullOrEmpty(nick))
            {
                _log.LogWarning($"[Plugin] Cannot kick Actor={actor}: NickName is empty");
                AppendRaceEvent("kick_result", new Dictionary<string, object?>
                {
                    ["actor"] = actor,
                    ["success"] = false,
                    ["reason"] = "no_nick_name"
                });
                return;
            }

            // Use game's own RPCKicked mechanism, found via reflection.
            _log.LogInfo($"[Plugin] Pre-kick state: NetworkClientState={PhotonNetwork.NetworkClientState} IsMasterClient={PhotonNetwork.IsMasterClient} InRoom={PhotonNetwork.InRoom}");
            var kicked = TryKickViaGameRpc(player);

            AppendRaceEvent("kick_result", new Dictionary<string, object?>
            {
                ["actor"] = actor,
                ["nick"] = nick,
                ["success"] = kicked,
                ["reason"] = kicked ? (object?)null : "game_rpc_failed"
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Plugin] Kick failed for Actor={actor}: {ex.Message}");
            AppendRaceEvent("kick_result", new Dictionary<string, object?>
            {
                ["actor"] = actor,
                ["success"] = false,
                ["reason"] = ex.Message
            });
        }
    }

    /// <summary>
    /// Calls Liftoff's own RPCKicked Photon RPC on the target player's PhotonView.
    /// The method and its parameter types are discovered at runtime via reflection so we
    /// don't need to hardcode obfuscated class names.
    /// </summary>
    private bool TryKickViaGameRpc(Player target)
    {
        try
        {
            // Find the RPCKicked method in Assembly-CSharp
            var gameDll = System.AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (gameDll == null)
            {
                _log.LogWarning("[Plugin] TryKickViaGameRpc: Assembly-CSharp not found");
                return false;
            }

            System.Reflection.MethodInfo? rpcMethod = null;
            System.Type? rpcType = null;
            foreach (var type in gameDll.GetTypes())
            {
                var m = type.GetMethod("RPCKicked",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (m != null)
                {
                    rpcMethod = m;
                    rpcType = type;
                    break;
                }
            }

            if (rpcMethod == null || rpcType == null)
            {
                _log.LogWarning("[Plugin] TryKickViaGameRpc: RPCKicked method not found in Assembly-CSharp");
                return false;
            }


            // Find a PhotonView in the scene that has this MonoBehaviour
            PhotonView? kickPhotonView = null;
            foreach (var pv in PhotonNetwork.PhotonViewCollection)
            {
                if (pv.GetComponent(rpcType) != null)
                {
                    kickPhotonView = pv;
                    break;
                }
            }

            if (kickPhotonView == null)
            {
                _log.LogWarning($"[Plugin] TryKickViaGameRpc: No PhotonView found with component {rpcType.Name}");
                return false;
            }

            // Build RPC params: create default instances of each parameter type.
            // RPCKicked(param1, param2):
            //   param2 (index 1) = auth context: Player field must be LocalPlayer so IsMasterClient is true
            //   param1 (index 0) = kick data: Player field set to target, bool fields set true for kick path
            var paramInfos = rpcMethod.GetParameters();
            var rpcParams = new object?[paramInfos.Length];
            for (int i = 0; i < paramInfos.Length; i++)
            {
                var pt = paramInfos[i].ParameterType;
                try
                {
                    var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(pt);
                    foreach (var field in pt.GetFields(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic))
                    {
                        if (field.FieldType == typeof(bool))
                        {
                            try { field.SetValue(instance, true); } catch { }
                        }
                        else if (field.FieldType == typeof(Player))
                        {
                            // param[1] = auth context → local player (master client) so IsMasterClient passes
                            // param[0] = kick data → target player
                            var playerVal = (i == 1) ? PhotonNetwork.LocalPlayer : target;
                            try { field.SetValue(instance, playerVal); } catch { }
                        }
                    }
                    rpcParams[i] = instance;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"[Plugin] TryKickViaGameRpc: Could not create param[{i}] ({pt.Name}): {ex.Message}");
                    rpcParams[i] = null;
                }
            }

            kickPhotonView.RPC("RPCKicked", target, rpcParams);
            _log.LogInfo($"[Plugin] TryKickViaGameRpc: RPC sent to Actor={target.ActorNumber}");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Plugin] TryKickViaGameRpc failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void EmitPlayerList()
    {
        try
        {
            if (!PhotonNetwork.InRoom) return;
            var players = PhotonNetwork.PlayerList;
            if (players == null || players.Length == 0) return;

            var playerData = players.Select(p => new Dictionary<string, object?>
            {
                ["actor"] = p.ActorNumber,
                ["nick"] = p.NickName,
                ["user_id"] = string.IsNullOrEmpty(p.UserId) ? (object?)null : p.UserId
            }).ToList<object?>();

            AppendRaceEvent("player_list", new Dictionary<string, object?>
            {
                ["players"] = playerData
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning($"EmitPlayerList failed: {ex.Message}");
        }
    }

    private void TryUnregister()
    {
        if (!_registered) return;

        try
        {
            PhotonNetwork.RemoveCallbackTarget(this);
        }
        catch
        {
            // ignore
        }

        _registered = false;
        _log.LogInfo("Unregistered Photon callback target.");
    }

    // High-frequency event codes that carry no competition value — skip file logging for these.
    // 201 = player position/movement telemetry (~10 Hz per player)
    // 226 = periodic room ping/heartbeat
    private static readonly HashSet<byte> _silentEventCodes = new() { 201, 226 };

    // Called for ALL Photon RaiseEvent messages received by this client
    public void OnEvent(EventData photonEvent)
    {
        try
        {
            ProcessRaceSignals(photonEvent);

            if (_silentEventCodes.Contains(photonEvent.Code))
                return;

            var sb = new StringBuilder(1024);
            sb.AppendLine($"[{DateTime.UtcNow:O}] EVENT Code={photonEvent.Code}");

            // Parameters is Dictionary<byte, object>
            if (photonEvent.Parameters != null)
            {
                foreach (var kv in photonEvent.Parameters)
                {
                    sb.AppendLine($"  Param[{kv.Key}] => {ObjectDescriber.Describe(kv.Value)}");
                }
            }
            else
            {
                sb.AppendLine("  (No parameters)");
            }

            AppendToFile(sb.ToString());
            AppendToFile(GetPerCodeFilePath(photonEvent.Code), sb.ToString());

        }
        catch (Exception ex)
        {
            _log.LogWarning($"OnEvent failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnPlayerEnteredRoom));
        _identity.SetPlayer(newPlayer.ActorNumber, newPlayer.NickName, newPlayer.UserId);
        AppendStateLine($"Player entered room: Actor={newPlayer.ActorNumber} Nick=\"{newPlayer.NickName}\" UserId=\"{newPlayer.UserId}\"");
        AppendRaceEvent("player_entered", new Dictionary<string, object?>
        {
            ["actor"] = newPlayer.ActorNumber,
            ["nick"] = newPlayer.NickName,
            ["user_id"] = string.IsNullOrEmpty(newPlayer.UserId) ? (object?)null : newPlayer.UserId
        });
        _multiplayerTrackControl?.OnPlayerEnteredRoom(newPlayer);
    }

    public void OnPlayerLeftRoom(Player otherPlayer)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnPlayerLeftRoom));
        _raceState.OnPlayerLeft(otherPlayer.ActorNumber);
        _identity.RemovePlayer(otherPlayer.ActorNumber);
        AppendStateLine($"Player left room: Actor={otherPlayer.ActorNumber} Nick=\"{otherPlayer.NickName}\"");
        AppendRaceEvent("player_left", new Dictionary<string, object?>
        {
            ["actor"] = otherPlayer.ActorNumber,
            ["nick"] = otherPlayer.NickName
        });
        _multiplayerTrackControl?.OnPlayerLeftRoom(otherPlayer);
    }

    public void OnRoomPropertiesUpdate(PhotonHashtable propertiesThatChanged)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnRoomPropertiesUpdate));
        if (RaceStateProjector.TryGetInt(propertiesThatChanged, "SGSO", out var sharedGameStateOffset) && sharedGameStateOffset == 1)
        {
            if (_raceState.ShouldStartNewRaceOnSgso())
                _raceState.StartNewRace("room_sgso_start");
        }

        AppendStateLine($"Room properties updated: {ObjectDescriber.Describe(propertiesThatChanged)}");
        _multiplayerTrackControl?.OnRoomPropertiesUpdate(propertiesThatChanged);
    }

    public void OnPlayerPropertiesUpdate(Player targetPlayer, PhotonHashtable changedProps)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnPlayerPropertiesUpdate));
        _identity.UpdateNick(targetPlayer.ActorNumber, targetPlayer.NickName);
        _raceState.UpdateRaceStateFromProperties(targetPlayer.ActorNumber, changedProps);
        AppendStateLine(
            $"Player properties updated: Actor={targetPlayer.ActorNumber} Nick=\"{targetPlayer.NickName}\" {ObjectDescriber.Describe(changedProps)}");
        _multiplayerTrackControl?.OnPlayerPropertiesUpdate(targetPlayer, changedProps);
    }

    public void OnMasterClientSwitched(Player newMasterClient)
    {
        _multiplayerTrackControl?.NotifyActivity(nameof(OnMasterClientSwitched));
        AppendStateLine($"Master client switched: Actor={newMasterClient.ActorNumber} Nick=\"{newMasterClient.NickName}\"");
        _multiplayerTrackControl?.OnMasterClientSwitched(newMasterClient);
    }

    private void AppendToFile(string text)
    {
        _logQueue.TryAdd((_filePath, text));
    }

    private void AppendToFile(string path, string text)
    {
        _logQueue.TryAdd((path, text));
    }

    private string GetPerCodeFilePath(byte eventCode)
    {
        return Path.Combine(_eventCodeDir, $"event-code-{eventCode}.log");
    }

    private void AppendStateLine(string message)
    {
        var line = $"[{DateTime.UtcNow:O}] {message}";
        _logQueue.TryAdd((_stateFilePath, line));
    }

    private void CleanOldLogs(string pluginDir, string eventCodeDir, int maxAgeDays)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            foreach (var dir in new[] { pluginDir, eventCodeDir })
            {
                foreach (var file in Directory.GetFiles(dir, "*.log").Concat(Directory.GetFiles(dir, "*.jsonl")))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
            }
        }
        catch { }
    }

    private void ProcessRaceSignals(EventData photonEvent)
    {
        _raceState.ProcessRaceSignals(photonEvent);
    }

    private void AppendRaceLine(string message)
    {
        var line = $"[{DateTime.UtcNow:O}] {message}";
        _logQueue.TryAdd((_raceFilePath, line));
        _log.LogInfo(line);
    }

    private void AppendRaceEvent(string eventType, Dictionary<string, object?> payload)
    {
        payload["event_type"] = eventType;
        payload["timestamp_utc"] = DateTime.UtcNow.ToString("O");
        payload["session_id"] = _sessionId;
        payload["race_id"] = _raceState.RaceId;
        payload["race_ordinal"] = _raceState.RaceOrdinal;
        payload["event_ordinal"] = ++_raceEventOrdinal;

        var json = JsonSerializer.SerializeJsonObject(payload);
        _logQueue.TryAdd((_raceJsonFilePath, json));
        _competitionClient?.EnqueueEvent(json);
    }

}
