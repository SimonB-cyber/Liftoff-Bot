using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiftoffPhotonEventLogger.Features.MultiplayerTrackControl;

internal sealed class MultiplayerDiscoveryService
{
    private static readonly string[] SearchTerms =
    {
        "multiplayer",
        "lobby",
        "room",
        "settings",
        "track",
        "race",
        "event",
        "photon",
        "workshop",
        "map",
        "content",
        "host",
        "master",
        "chat",
        "message",
        "history",
        "window"
    };

    private static readonly string[] KnownTypeNames =
    {
        "CurrentContentContainer",
        "InGameMenuMainPanel",
        "MultiplayerRaceScoreButtonPanel",
        "Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup",
        "Liftoff.Multiplayer.GameSetup.ContentSettingsPanel",
        "Liftoff.Multiplayer.GameSetup.RoomSettingsPanel",
        "Liftoff.Multiplayer.GameSetup.DroneSettingsPanel",
        "Liftoff.Multiplayer.GameSetup.GameModifiersPanel",
        "Liftoff.Multiplayer.GameContentEntry",
        "Liftoff.Multiplayer.LevelInitMultiplayer",
        "Liftoff.Multiplayer.GameSetup.LastMultiplayerSession",
        "TrackQuickInfo",
        "RaceQuickInfo",
        "ShareableContent",
        "Liftoff.Multiplayer.GameMode",
        "LiftoffDropdown",
        "Liftoff.Multiplayer.Chat.ChatHistory",
        "Liftoff.Multiplayer.Chat.ChatWindowPanel",
        "Liftoff.Multiplayer.Chat.ChatToggle",
        "Liftoff.Multiplayer.Chat.MessageEntryPanel",
        "Liftoff.Multiplayer.Chat.Messages.PlayerChatMessage",
        "Liftoff.Multiplayer.Chat.Messages.PlayerUpdateMessage",
        "Liftoff.Multiplayer.Chat.Messages.SystemChatMessage"
    };

    private readonly MultiplayerTrackControlLog _log;
    private readonly Func<object?, string> _describe;
    private readonly Dictionary<string, Type> _knownTypes = new(StringComparer.Ordinal);
    private readonly List<DiscoveredTypeCandidate> _candidates = new();
    private bool _scanned;

    public MultiplayerDiscoveryService(MultiplayerTrackControlLog log, Func<object?, string> describe)
    {
        _log = log;
        _describe = describe;
    }

    public IReadOnlyList<DiscoveredTypeCandidate> Candidates => _candidates;

    public void Refresh(bool force = false)
    {
        if (_scanned && !force)
            return;

        _knownTypes.Clear();
        _candidates.Clear();

        foreach (var type in AppDomain.CurrentDomain
                     .GetAssemblies()
                     .Where(assembly => !assembly.IsDynamic)
                     .SelectMany(GetLoadableTypes))
        {
            TryAddKnownType(type);

            var candidate = ScoreType(type);
            if (candidate.Score > 0)
                _candidates.Add(candidate);
        }

        _candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        _scanned = true;
    }

    public Type? TryResolveKnownType(string fullName)
    {
        Refresh();
        _knownTypes.TryGetValue(fullName, out var type);
        return type;
    }

    public void DumpDiscovery(int topCount = 40)
    {
        Refresh();
        _log.Info("DISCOVERY", $"Loaded assemblies: {AppDomain.CurrentDomain.GetAssemblies().Length}; candidates: {_candidates.Count}");

        foreach (var fullName in KnownTypeNames)
        {
            var type = TryResolveKnownType(fullName);
            if (type == null)
            {
                _log.Warn("DISCOVERY", $"Known type missing: {fullName}");
                continue;
            }

            _log.Info("DISCOVERY", $"Known type: {type.FullName} (assembly={type.Assembly.GetName().Name})");
            DumpInterestingMembers(type, 8);
            DumpLiveObjects(type, 2);
        }

        foreach (var candidate in _candidates.Take(topCount))
        {
            _log.Info("DISCOVERY", $"Candidate score={candidate.Score} type={candidate.Type.FullName} reasons={string.Join("|", candidate.Reasons)}");
            foreach (var method in candidate.Methods.Take(6))
                _log.Info("DISCOVERY", $"  method {ReflectionHelper.FormatMethodSignature(method)}");
            foreach (var property in candidate.Properties.Take(6))
                _log.Info("DISCOVERY", $"  property {property.PropertyType.Name} {candidate.Type.FullName}.{property.Name}");
        }
    }

    private void DumpInterestingMembers(Type type, int limit)
    {
        var interestingMethods = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => SearchTerms.Any(term => method.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            .Take(limit)
            .ToList();

        foreach (var method in interestingMethods)
            _log.Info("DISCOVERY", $"  method {ReflectionHelper.FormatMethodSignature(method)}");

        var interestingProperties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => SearchTerms.Any(term => property.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            .Take(limit)
            .ToList();

        foreach (var property in interestingProperties)
            _log.Info("DISCOVERY", $"  property {property.PropertyType.Name} {type.FullName}.{property.Name}");
    }

    private void DumpLiveObjects(Type type, int limit)
    {
        foreach (var liveObject in ReflectionHelper.GetLiveObjects(type).Take(limit))
        {
            _log.Info("DISCOVERY", $"  live {ReflectionHelper.DescribeObjectIdentity(liveObject)}");
            _log.Info("DISCOVERY", $"  snapshot {ReflectionHelper.SafeDescribe(_describe, liveObject)}");
        }
    }

    private void TryAddKnownType(Type type)
    {
        foreach (var fullName in KnownTypeNames)
        {
            if (string.Equals(type.FullName, fullName, StringComparison.Ordinal))
            {
                _knownTypes[fullName] = type;
            }
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static DiscoveredTypeCandidate ScoreType(Type type)
    {
        var reasons = new List<string>();
        var score = 0;
        var fullName = type.FullName ?? type.Name;

        foreach (var term in SearchTerms)
        {
            if (fullName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 10;
                reasons.Add($"type:{term}");
            }
        }

        if (type.Namespace != null && type.Namespace.IndexOf("Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 20;
            reasons.Add("namespace:multiplayer");
        }

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var method in methods)
        {
            foreach (var term in SearchTerms)
            {
                if (method.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 3;
                    reasons.Add($"method:{term}");
                    break;
                }
            }
        }

        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var property in properties)
        {
            foreach (var term in SearchTerms)
            {
                if (property.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 2;
                    reasons.Add($"property:{term}");
                    break;
                }
            }
        }

        return new DiscoveredTypeCandidate(type, score, reasons.Distinct().ToList(), methods.ToList(), properties.ToList());
    }

    internal sealed class DiscoveredTypeCandidate
    {
        public DiscoveredTypeCandidate(Type type, int score, List<string> reasons, List<MethodInfo> methods, List<PropertyInfo> properties)
        {
            Type = type;
            Score = score;
            Reasons = reasons;
            Methods = methods;
            Properties = properties;
        }

        public Type Type { get; }
        public int Score { get; }
        public List<string> Reasons { get; }
        public List<MethodInfo> Methods { get; }
        public List<PropertyInfo> Properties { get; }
    }
}
