using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using LiftoffPhotonEventLogger.Features.Identity;
using LiftoffPhotonEventLogger.Features.Logging;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace LiftoffPhotonEventLogger.Features.Racing;

/// <summary>
/// Owns the race state machine: lap recording, race boundaries, pilot completion,
/// GMS merging, and checkpoint extraction. Calls back to the plugin for event
/// emission and logging via delegates injected at construction.
/// </summary>
internal sealed class RaceStateProjector
{
    private readonly PlayerIdentityStore _identity;
    private readonly ManualLogSource _log;
    private readonly Action<string> _appendRaceLine;
    private readonly Action<string, Dictionary<string, object?>> _appendRaceEvent;
    private readonly ConfigEntry<int> _minLapMs;
    private readonly ConfigEntry<int> _maxLapsPerRace;

    private const int ClassicRaceLapCount = 3;
    private static readonly string LapTimesSuffix = "_laptimes";
    private static readonly Regex GmsLapArrayRegex = new(@"Single\[\]\[(?<count>\d+)\]\s\[(?<vals>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex GmsLapValueRegex = new(@"float\s(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex GmsCheckpointRegex = new(@"RacePlayerCheckpointInfo\s\{ID=string\s""(?<id>[^""]+)"",\sLap=int\s(?<lap>\d+),\sTime=float\s(?<time>-?\d+(?:\.\d+)?)\}", RegexOptions.Compiled);

    private string _raceId;
    private int _raceOrdinal;
    private bool _raceEndEmitted;
    private readonly HashSet<int> _raceParticipants = new();
    private readonly Dictionary<int, PilotLapState> _actorLapState = new();
    private readonly Dictionary<string, PilotLapState> _guidLapState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _actorToRaceState = new();
    private readonly Dictionary<int, string> _actorLastCheckpointId = new();
    private readonly Dictionary<int, int> _actorGmsBaseline = new();
    private bool _needGmsBaseline = true;
    private DateTime _suppressEvent200Until = DateTime.MinValue;

    public string RaceId => _raceId;
    public int RaceOrdinal => _raceOrdinal;
    public int LapStateCount => _actorLapState.Count;
    public bool RaceEndEmitted => _raceEndEmitted;

    internal sealed class PilotLapState
    {
        public int Actor;
        public string Nick = "Unknown";
        public string Guid = string.Empty;
        public readonly List<int> LapTimesMs = new();
        public bool IsComplete;
        public string CompletionReason = string.Empty;
    }

    public RaceStateProjector(
        PlayerIdentityStore identity,
        ManualLogSource log,
        Action<string> appendRaceLine,
        Action<string, Dictionary<string, object?>> appendRaceEvent,
        ConfigEntry<int> minLapMs,
        ConfigEntry<int> maxLapsPerRace,
        string initialRaceId,
        int initialRaceOrdinal)
    {
        _identity = identity;
        _log = log;
        _appendRaceLine = appendRaceLine;
        _appendRaceEvent = appendRaceEvent;
        _minLapMs = minLapMs;
        _maxLapsPerRace = maxLapsPerRace;
        _raceId = initialRaceId;
        _raceOrdinal = initialRaceOrdinal;
    }

    public void SuppressLapEventsUntil(DateTime until)
    {
        _suppressEvent200Until = until;
    }

    public void OnPlayerLeft(int actor)
    {
        _raceParticipants.Remove(actor);
        TryEmitRaceEnd();
    }

    public void ProcessRaceSignals(EventData photonEvent)
    {
        if (!TryExtractLapTime(photonEvent, out var actor, out var guid, out var lapMs))
            return;

        RecordLapTime(actor, guid, lapMs, "event200");
    }

    public void UpdateRaceStateFromProperties(int actor, PhotonHashtable changedProps)
    {
        if (TryGetInt(changedProps, "GS", out var gs))
        {
            if (gs == 2)
                _raceParticipants.Add(actor);
        }

        if (_actorToRaceState.TryGetValue(actor, out var previousRs)
            && TryGetInt(changedProps, "RS", out var nextRs)
            && previousRs >= 5 && nextRs <= 3)
        {
            StartNewRace($"actor_{actor}_rs_reset");
        }

        if (changedProps.TryGetValue("GMS", out var gmsObj) && gmsObj != null)
        {
            var gmsText = ObjectDescriber.Describe(gmsObj);

            if (TryExtractCheckpointFromGmsText(gmsText, out var checkpointId, out var checkpointLap, out var checkpointTimeSec))
            {
                if (!_actorLastCheckpointId.TryGetValue(actor, out var prevCheckpointId) || !string.Equals(prevCheckpointId, checkpointId, StringComparison.Ordinal))
                {
                    _actorLastCheckpointId[actor] = checkpointId;
                    _appendRaceLine(
                        $"CHECKPOINT actor={actor} nick=\"{_identity.ResolveNick(actor)}\" checkpointId={checkpointId} lap={checkpointLap} timeSec={checkpointTimeSec:0.000}");
                    _appendRaceEvent("checkpoint", new Dictionary<string, object?>
                    {
                        ["actor"] = actor,
                        ["nick"] = _identity.ResolveNick(actor),
                        ["checkpoint_id"] = checkpointId,
                        ["lap_index"] = checkpointLap,
                        ["elapsed_sec"] = Math.Round(checkpointTimeSec, 3)
                    });
                }
            }

            if (TryExtractLapTimesFromGmsText(gmsText, out var lapTimesSec))
            {
                MergeGmsLapSeries(actor, lapTimesSec);
            }
        }

        if (TryGetInt(changedProps, "RS", out var rs))
        {
            var hadLowerRs = _actorToRaceState.TryGetValue(actor, out var prevRs) && prevRs < 5;
            _actorToRaceState[actor] = rs;
            if (rs >= 5 && hadLowerRs)
            {
                var state = GetOrCreatePilotLapState(actor, string.Empty);
                if (!state.IsComplete)
                {
                    state.IsComplete = true;
                    state.CompletionReason = "race_state_finished";
                    EmitPilotComplete(state);
                }
                TryEmitRaceEnd();
            }
        }
    }

    public bool ShouldStartNewRaceOnSgso()
    {
        return _raceEndEmitted || _actorLapState.Count > 0;
    }

    public void StartNewRace(string reason)
    {
        var previousRaceId = _raceId;
        _raceId = System.Guid.NewGuid().ToString("N");
        _raceOrdinal++;
        _raceEndEmitted = false;
        _actorLapState.Clear();
        _guidLapState.Clear();
        _actorToRaceState.Clear();
        _raceParticipants.Clear();
        _actorLastCheckpointId.Clear();
        _actorGmsBaseline.Clear();
        _needGmsBaseline = false;
        _appendRaceLine($"RACE_RESET reason={reason}");
        _appendRaceEvent("race_reset", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["previous_race_id"] = previousRaceId,
            ["race_ordinal"] = _raceOrdinal
        });
    }

    private bool TryExtractLapTime(EventData photonEvent, out int actor, out string guid, out int lapMs)
    {
        actor = -1;
        guid = string.Empty;
        lapMs = 0;

        if (photonEvent.Code != 200 || photonEvent.Parameters == null)
            return false;

        if (!photonEvent.Parameters.TryGetValue(254, out var actorObj) || !TryConvertToInt(actorObj, out actor))
            return false;

        if (!photonEvent.Parameters.TryGetValue(245, out var payloadObj) || payloadObj is not PhotonHashtable payload)
            return false;

        if (!TryGetInt(payload, (byte)5, out var action) || action != 15)
            return false;

        if (!TryGetInt(payload, (byte)0, out var category) || category != 1)
            return false;

        if (!payload.TryGetValue((byte)4, out var dataObj) || dataObj is not object[] data || data.Length < 3)
            return false;

        if (data[1] is not string key || !key.EndsWith(LapTimesSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryConvertToInt(data[2], out lapMs))
            return false;

        guid = key.Substring(0, key.Length - LapTimesSuffix.Length);
        return true;
    }

    private PilotLapState GetOrCreatePilotLapState(int actor, string guid)
    {
        if (_actorLapState.TryGetValue(actor, out var existing))
        {
            if (string.IsNullOrEmpty(existing.Guid))
                existing.Guid = guid;
            if (!_guidLapState.ContainsKey(existing.Guid))
                _guidLapState[existing.Guid] = existing;
            existing.Nick = _identity.ResolveNick(actor);
            return existing;
        }

        if (!string.IsNullOrEmpty(guid) && _guidLapState.TryGetValue(guid, out var byGuid))
        {
            byGuid.Actor = actor;
            byGuid.Nick = _identity.ResolveNick(actor);
            _actorLapState[actor] = byGuid;
            return byGuid;
        }

        var state = new PilotLapState
        {
            Actor = actor,
            Nick = _identity.ResolveNick(actor),
            Guid = guid
        };
        _actorLapState[actor] = state;
        if (!string.IsNullOrEmpty(guid))
            _guidLapState[guid] = state;
        return state;
    }

    private void MergeGmsLapSeries(int actor, List<float> lapTimesSec)
    {
        var incoming = lapTimesSec.Select(v => (int)Math.Round(v * 1000d)).ToList();
        if (incoming.Count == 0)
            return;

        var state = GetOrCreatePilotLapState(actor, string.Empty);
        var existing = state.LapTimesMs;

        if (state.IsComplete)
            return;

        if (_actorGmsBaseline.TryGetValue(actor, out var baseline))
        {
            if (incoming.Count <= baseline)
                return;
            incoming = incoming.Skip(baseline).ToList();
        }
        else if (existing.Count == 0 && _needGmsBaseline)
        {
            _actorGmsBaseline[actor] = incoming.Count;
            _log.LogInfo($"[Recording] GMS baseline set for actor {actor}: {incoming.Count} pre-session lap(s) skipped");
            return;
        }

        if (existing.Count == 0)
        {
            foreach (var lapMs in incoming)
                RecordLapTime(actor, string.Empty, lapMs, "gms");
            return;
        }

        if (IsPrefix(existing, incoming))
            return;

        if (IsPrefix(incoming, existing))
        {
            for (var i = existing.Count; i < incoming.Count; i++)
                RecordLapTime(actor, string.Empty, incoming[i], "gms");
            return;
        }

        ResetPilotState(actor, "gms_series_mismatch");
        foreach (var lapMs in incoming)
            RecordLapTime(actor, string.Empty, lapMs, "gms");
    }

    private void RecordLapTime(int actor, string guid, int lapMs, string source)
    {
        if (DateTime.UtcNow < _suppressEvent200Until)
        {
            _log.LogWarning($"[Recording] Lap suppressed (track change grace): actor={actor} lapMs={lapMs} source={source}");
            return;
        }

        var minMs = _minLapMs.Value;
        if (minMs > 0 && lapMs < minMs)
        {
            _log.LogWarning($"[Recording] Lap ignored (too short): actor={actor} lapMs={lapMs} minLapMs={minMs}");
            return;
        }

        var state = GetOrCreatePilotLapState(actor, guid);
        if (state.IsComplete)
            return;

        var nextLapIndex = state.LapTimesMs.Count;
        if (nextLapIndex > 0 && state.LapTimesMs[nextLapIndex - 1] == lapMs)
            return;

        state.LapTimesMs.Add(lapMs);
        _raceParticipants.Add(actor);

        var lapNumber = state.LapTimesMs.Count;
        var deltaPrev = lapNumber > 1 ? lapMs - state.LapTimesMs[lapNumber - 2] : (int?)null;
        var bestBefore = lapNumber > 1 ? state.LapTimesMs.Take(lapNumber - 1).Min() : lapMs;
        var deltaBest = lapNumber > 1 ? lapMs - bestBefore : (int?)null;

        _appendRaceLine(
            $"LAP actor={actor} nick=\"{state.Nick}\" guid={state.Guid} source={source} lap={lapNumber} ms={lapMs} sec={ToSeconds(lapMs)} deltaPrevMs={FormatNullable(deltaPrev)} deltaBestMs={FormatNullable(deltaBest)}");
        var steamId = _identity.ResolveUserId(actor);
        _appendRaceEvent("lap_recorded", new Dictionary<string, object?>
        {
            ["actor"] = actor,
            ["nick"] = state.Nick,
            ["pilot_guid"] = state.Guid,
            ["steam_id"] = string.IsNullOrEmpty(steamId) ? (object?)null : steamId,
            ["source"] = source,
            ["lap_number"] = lapNumber,
            ["lap_ms"] = lapMs,
            ["lap_sec"] = Math.Round(lapMs / 1000d, 3),
            ["delta_prev_ms"] = deltaPrev,
            ["delta_best_ms"] = deltaBest
        });

        var maxLaps = _maxLapsPerRace.Value;
        if (maxLaps > 0 && !state.IsComplete && lapNumber >= maxLaps)
        {
            state.IsComplete = true;
            state.CompletionReason = "lap_count_reached";
            EmitPilotComplete(state);
        }

        TryEmitRaceEnd();
    }

    private static bool IsPrefix(IReadOnlyList<int> source, IReadOnlyList<int> candidatePrefix)
    {
        if (candidatePrefix.Count > source.Count)
            return false;

        for (var i = 0; i < candidatePrefix.Count; i++)
        {
            if (source[i] != candidatePrefix[i])
                return false;
        }

        return true;
    }

    private void ResetPilotState(int actor, string reason)
    {
        if (_actorLapState.TryGetValue(actor, out var state))
        {
            if (!string.IsNullOrEmpty(state.Guid))
                _guidLapState.Remove(state.Guid);
        }

        _actorLapState.Remove(actor);
        _actorLastCheckpointId.Remove(actor);
        _actorGmsBaseline.Remove(actor);
        _appendRaceLine($"PILOT_RESET actor={actor} nick=\"{_identity.ResolveNick(actor)}\" reason={reason}");
        _appendRaceEvent("pilot_reset", new Dictionary<string, object?>
        {
            ["actor"] = actor,
            ["nick"] = _identity.ResolveNick(actor),
            ["reason"] = reason
        });
    }

    private bool TryExtractLapTimesFromGmsText(string gmsText, out List<float> lapTimesSec)
    {
        lapTimesSec = new List<float>();

        var matches = GmsLapArrayRegex.Matches(gmsText);
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["count"].Value, out var expectedCount))
                continue;

            var values = new List<float>();
            var valueMatches = GmsLapValueRegex.Matches(match.Groups["vals"].Value);
            foreach (Match valueMatch in valueMatches)
            {
                if (float.TryParse(valueMatch.Groups["v"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            if (values.Count != expectedCount)
                continue;
            var gmsLapCap = _maxLapsPerRace.Value > 0 ? _maxLapsPerRace.Value : 100;
            if (values.Count == 0 || values.Count > gmsLapCap)
                continue;
            if (values.Any(v => v <= 1f || v > 600f))
                continue;

            if (values.Count > lapTimesSec.Count)
                lapTimesSec = values;
        }

        return lapTimesSec.Count > 0;
    }

    private bool TryExtractCheckpointFromGmsText(string gmsText, out string checkpointId, out int lap, out float timeSec)
    {
        checkpointId = string.Empty;
        lap = 0;
        timeSec = 0f;

        var match = GmsCheckpointRegex.Match(gmsText);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["lap"].Value, out lap))
            return false;
        if (!float.TryParse(match.Groups["time"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out timeSec))
            return false;

        checkpointId = match.Groups["id"].Value;
        return checkpointId.Length > 0;
    }

    private void EmitPilotComplete(PilotLapState state)
    {
        var laps = string.Join(",", state.LapTimesMs);
        var totalMs = state.LapTimesMs.Take(ClassicRaceLapCount).Sum();
        _appendRaceLine(
            $"PILOT_COMPLETE actor={state.Actor} nick=\"{state.Nick}\" guid={state.Guid} reason={state.CompletionReason} lapsLogged={state.LapTimesMs.Count} lapTimesMs=[{laps}] totalMs={totalMs} totalSec={ToSeconds(totalMs)}");
        _appendRaceEvent("pilot_complete", new Dictionary<string, object?>
        {
            ["actor"] = state.Actor,
            ["nick"] = state.Nick,
            ["pilot_guid"] = state.Guid,
            ["reason"] = state.CompletionReason,
            ["laps_logged"] = state.LapTimesMs.Count,
            ["lap_times_ms"] = state.LapTimesMs.ToArray(),
            ["total_ms"] = totalMs,
            ["total_sec"] = Math.Round(totalMs / 1000d, 3)
        });
    }

    private void TryEmitRaceEnd()
    {
        if (_raceEndEmitted || _raceParticipants.Count == 0)
            return;

        foreach (var actor in _raceParticipants)
        {
            var pilotDone = _actorLapState.TryGetValue(actor, out var pilot) && pilot.IsComplete;
            var rsDone = _actorToRaceState.TryGetValue(actor, out var rs) && rs >= 5;
            if (!pilotDone && !rsDone)
                return;
        }

        _raceEndEmitted = true;

        var ranked = _actorLapState.Values
            .Where(p => p.LapTimesMs.Count >= ClassicRaceLapCount)
            .Select(p => new
            {
                p.Actor,
                p.Nick,
                TotalMs = p.LapTimesMs.Take(ClassicRaceLapCount).Sum()
            })
            .OrderBy(p => p.TotalMs)
            .ToList();

        if (ranked.Count > 0)
        {
            var winner = ranked[0];
            _appendRaceLine(
                $"RACE_END participants={_raceParticipants.Count} completed={_raceParticipants.Count} winnerActor={winner.Actor} winnerNick=\"{winner.Nick}\" winnerTotalMs={winner.TotalMs} winnerTotalSec={ToSeconds(winner.TotalMs)}");
            _appendRaceEvent("race_end", new Dictionary<string, object?>
            {
                ["participants"] = _raceParticipants.Count,
                ["completed"] = _raceParticipants.Count,
                ["winner_actor"] = winner.Actor,
                ["winner_nick"] = winner.Nick,
                ["winner_total_ms"] = winner.TotalMs,
                ["winner_total_sec"] = Math.Round(winner.TotalMs / 1000d, 3)
            });
            return;
        }

        _appendRaceLine($"RACE_END participants={_raceParticipants.Count} completed={_raceParticipants.Count}");
        _appendRaceEvent("race_end", new Dictionary<string, object?>
        {
            ["participants"] = _raceParticipants.Count,
            ["completed"] = _raceParticipants.Count
        });
    }

    internal static bool TryGetInt(PhotonHashtable map, object key, out int value)
    {
        value = 0;
        return map.TryGetValue(key, out var raw) && TryConvertToInt(raw, out value);
    }

    internal static bool TryConvertToInt(object? raw, out int value)
    {
        switch (raw)
        {
            case byte b:
                value = b;
                return true;
            case short s:
                value = s;
                return true;
            case int i:
                value = i;
                return true;
            case long l when l is <= int.MaxValue and >= int.MinValue:
                value = (int)l;
                return true;
            case string str when int.TryParse(str, out var parsed):
                value = parsed;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static string ToSeconds(int ms)
    {
        return (ms / 1000d).ToString("0.000");
    }

    private static string FormatNullable(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "NA";
    }
}
