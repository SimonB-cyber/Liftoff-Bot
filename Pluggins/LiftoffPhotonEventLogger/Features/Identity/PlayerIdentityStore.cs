using System;
using System.Collections.Generic;

namespace LiftoffPhotonEventLogger.Features.Identity;

/// <summary>
/// Tracks the mapping between Photon actor numbers and player identity
/// (nickname, user/steam ID). Single source of truth for "who is actor N?"
/// </summary>
internal sealed class PlayerIdentityStore
{
    private readonly Dictionary<int, string> _actorToNick = new();
    private readonly Dictionary<int, string> _actorToUserId = new();

    public void SetPlayer(int actor, string nick, string? userId)
    {
        _actorToNick[actor] = nick;
        if (!string.IsNullOrEmpty(userId))
            _actorToUserId[actor] = userId!;
    }

    public void UpdateNick(int actor, string nick)
    {
        _actorToNick[actor] = nick;
    }

    public void RemovePlayer(int actor)
    {
        _actorToNick.Remove(actor);
        _actorToUserId.Remove(actor);
    }

    public string ResolveNick(int actor)
    {
        return _actorToNick.TryGetValue(actor, out var nick) ? nick : $"Actor{actor}";
    }

    public string? ResolveUserId(int actor)
    {
        return _actorToUserId.TryGetValue(actor, out var uid) ? uid : null;
    }

    public bool TryFindActorByUserId(string userId, out int actor)
    {
        foreach (var kv in _actorToUserId)
        {
            if (string.Equals(kv.Value, userId, StringComparison.OrdinalIgnoreCase))
            {
                actor = kv.Key;
                return true;
            }
        }
        actor = -1;
        return false;
    }
}
