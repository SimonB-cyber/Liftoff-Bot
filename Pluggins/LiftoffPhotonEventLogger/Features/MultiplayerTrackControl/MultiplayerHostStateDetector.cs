using System;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;

namespace LiftoffPhotonEventLogger.Features.MultiplayerTrackControl;

internal sealed class MultiplayerHostStateDetector
{
    private readonly MultiplayerDiscoveryService _discovery;
    private readonly MultiplayerTrackControlLog _log;
    private readonly Func<object?, string> _describe;

    public MultiplayerHostStateDetector(
        MultiplayerDiscoveryService discovery,
        MultiplayerTrackControlLog log,
        Func<object?, string> describe)
    {
        _discovery = discovery;
        _log = log;
        _describe = describe;
    }

    public HostStateSnapshot Capture()
    {
        _discovery.Refresh();

        var snapshot = new HostStateSnapshot();
        var currentContentContainerType = _discovery.TryResolveKnownType("CurrentContentContainer");
        var currentContentContainers = ReflectionHelper.GetLiveObjects(currentContentContainerType);
        var currentContentContainer = currentContentContainers.Count > 0
            ? currentContentContainers[0]
            : null;

        snapshot.CurrentContentContainer = currentContentContainer;
        snapshot.SessionSettings = ReflectionHelper.GetMemberValue(currentContentContainer, "SessionSettings");
        snapshot.Level = ReflectionHelper.GetMemberValue(currentContentContainer, "Level");
        snapshot.Track = ReflectionHelper.GetMemberValue(currentContentContainer, "Track");
        snapshot.Race = ReflectionHelper.GetMemberValue(currentContentContainer, "Race");

        if (currentContentContainer != null && ReflectionHelper.IsTruthy(ReflectionHelper.GetMemberValue(currentContentContainer, "InMultiplayer")))
        {
            snapshot.IsInMultiplayer = true;
            snapshot.InMultiplayerReason = "CurrentContentContainer.InMultiplayer";
        }
        else if (PhotonNetwork.InRoom)
        {
            snapshot.IsInMultiplayer = true;
            snapshot.InMultiplayerReason = "PhotonNetwork.InRoom";
        }
        else
        {
            snapshot.IsInMultiplayer = false;
            snapshot.InMultiplayerReason = "No multiplayer room detected";
        }

        snapshot.CurrentRoom = PhotonNetwork.CurrentRoom;
        snapshot.LocalPlayer = PhotonNetwork.LocalPlayer;

        if (PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
        {
            snapshot.IsHost = true;
            snapshot.HostReason = "PhotonNetwork.IsMasterClient";
        }
        else if (PhotonNetwork.CurrentRoom != null && PhotonNetwork.LocalPlayer != null)
        {
            snapshot.IsHost = PhotonNetwork.CurrentRoom.MasterClientId == PhotonNetwork.LocalPlayer.ActorNumber;
            snapshot.HostReason = $"Photon room MasterClientId={PhotonNetwork.CurrentRoom.MasterClientId} LocalActor={PhotonNetwork.LocalPlayer.ActorNumber}";
        }
        else
        {
            snapshot.IsHost = false;
            snapshot.HostReason = "Photon host state unavailable";
        }

        snapshot.RoomProperties = PhotonNetwork.CurrentRoom?.CustomProperties;
        return snapshot;
    }

    public void LogSnapshot(string category)
    {
        var snapshot = Capture();
        _log.Info(category, $"IsInMultiplayer={snapshot.IsInMultiplayer} reason={snapshot.InMultiplayerReason}");
        _log.Info(category, $"IsHost={snapshot.IsHost} reason={snapshot.HostReason}");

        if (snapshot.CurrentRoom != null)
        {
            _log.Info(category, $"Room name={snapshot.CurrentRoom.Name} players={snapshot.CurrentRoom.PlayerCount}/{snapshot.CurrentRoom.MaxPlayers} open={snapshot.CurrentRoom.IsOpen} visible={snapshot.CurrentRoom.IsVisible}");
            _log.Info(category, $"Room custom properties={ReflectionHelper.SafeDescribe(_describe, snapshot.RoomProperties)}");
        }

        if (snapshot.SessionSettings != null)
            _log.Info(category, $"Session settings={ReflectionHelper.SafeDescribe(_describe, snapshot.SessionSettings)}");
        if (snapshot.Track != null)
            _log.Info(category, $"Current track={ReflectionHelper.SafeDescribe(_describe, snapshot.Track)}");
        if (snapshot.Race != null)
            _log.Info(category, $"Current race={ReflectionHelper.SafeDescribe(_describe, snapshot.Race)}");
    }

    internal sealed class HostStateSnapshot
    {
        public bool IsInMultiplayer { get; set; }
        public bool IsHost { get; set; }
        public string InMultiplayerReason { get; set; } = string.Empty;
        public string HostReason { get; set; } = string.Empty;
        public object? CurrentContentContainer { get; set; }
        public object? SessionSettings { get; set; }
        public object? Level { get; set; }
        public object? Track { get; set; }
        public object? Race { get; set; }
        public Room? CurrentRoom { get; set; }
        public Player? LocalPlayer { get; set; }
        public Hashtable? RoomProperties { get; set; }
    }
}
