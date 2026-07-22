using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Potok.Backend.Infrastructure.Gateway.Hubs;

public sealed record RoomMemberInfo(string RoomId, string ParticipantId, string Role);

// Minimal registry of live co-watch rooms and their members. Lets the hub answer "is this room actually alive?"
// (a guest opening a stale share link for a room whose host left long ago) and reap leaked entries.
//
// In-memory, single gateway instance. It's behind this interface on purpose: a multi-instance / survive-restart
// deployment can swap in a Redis-backed implementation without touching the hub. See WatchTogetherHub notes.
public interface IRoomStore
{
    // Register a connection as a member of a room (creates the room if new). Idempotent per connection.
    void AddMember(string roomId, string connectionId, string participantId, string role);

    // Remove a connection; returns its member info for the disconnect announce, or null if it wasn't tracked.
    RoomMemberInfo? RemoveMember(string connectionId);

    // Whether the room currently has at least one connected host — the authority. No host ⇒ the room is dead.
    bool HasHost(string roomId);

    // Bump the room's last-activity timestamp (called on every broadcast).
    void Touch(string roomId);

    // Reap empty rooms and rooms idle past ttl (a hard ceiling against leaked entries). Returns reaped room ids.
    IReadOnlyList<string> Reap(TimeSpan idleTtl);
}

public sealed class RoomStore : IRoomStore
{
    private sealed class Room
    {
        public DateTimeOffset LastSeen = DateTimeOffset.UtcNow;
        public readonly ConcurrentDictionary<string, string> Members = new(); // connectionId → role
    }

    private readonly ConcurrentDictionary<string, RoomMemberInfo> _byConnection = new();
    private readonly ConcurrentDictionary<string, Room> _rooms = new();

    public void AddMember(string roomId, string connectionId, string participantId, string role)
    {
        _byConnection[connectionId] = new RoomMemberInfo(roomId, participantId, role);
        var room = _rooms.GetOrAdd(roomId, _ => new Room());
        room.Members[connectionId] = role;
        room.LastSeen = DateTimeOffset.UtcNow;
    }

    public RoomMemberInfo? RemoveMember(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var member)) return null;
        if (_rooms.TryGetValue(member.RoomId, out var room))
        {
            room.Members.TryRemove(connectionId, out _);
            room.LastSeen = DateTimeOffset.UtcNow;
            if (room.Members.IsEmpty) _rooms.TryRemove(member.RoomId, out _);
        }
        return member;
    }

    public bool HasHost(string roomId)
        => _rooms.TryGetValue(roomId, out var room) && room.Members.Values.Any(r => r == "host");

    public void Touch(string roomId)
    {
        if (_rooms.TryGetValue(roomId, out var room)) room.LastSeen = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<string> Reap(TimeSpan idleTtl)
    {
        var cutoff = DateTimeOffset.UtcNow - idleTtl;
        var reaped = new List<string>();
        foreach (var kv in _rooms)
        {
            // Normal cleanup is RemoveMember (immediate on disconnect); this is a safety net for leaked entries.
            if (kv.Value.Members.IsEmpty || kv.Value.LastSeen < cutoff)
            {
                if (_rooms.TryRemove(kv.Key, out _)) reaped.Add(kv.Key);
            }
        }
        return reaped;
    }
}
