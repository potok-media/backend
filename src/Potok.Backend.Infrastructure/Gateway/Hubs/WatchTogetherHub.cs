using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Potok.Backend.Infrastructure.Gateway.Hubs;

// Co-watching ("watch together") sync hub. A host mints a random room key (base64url on the client) and
// shares a link; guests JoinRoom the same key. The host is the sole authority and broadcasts opaque JSON
// messages (timing/pause/control/lifecycle) to the room via OthersInGroup (sender gets no echo).
//
// Presence + room lifecycle live in IRoomStore. JoinRoom records connectionId → {room, participantId, role}
// so an ABRUPT disconnect (tab close / network drop, no explicit LeaveRoom) is announced to the room as a
// leave — the server-side backstop for the client-authored participant-left/host-ended (which only fire on an
// explicit Leave). The store also lets us reject a guest opening a stale link for a room whose host is gone.
// In-memory (single gateway instance); a multi-instance deployment would swap in a Redis-backed IRoomStore.
//
// Note: string hub-method parameters bind from the client only because implicit from-services binding is
// disabled in Program.cs (a connection string is registered as a singleton `string`).
public class WatchTogetherHub : Hub
{
    private readonly IRoomStore _rooms;

    public WatchTogetherHub(IRoomStore rooms) => _rooms = rooms;

    public async Task JoinRoom(string roomId, string participantId, string role)
    {
        // A guest opening a stale share link for a room whose host already left: don't let them sit forever
        // waiting for someone who isn't there — tell them the room is dead and don't join the group.
        if (role != "host" && !_rooms.HasHost(roomId))
        {
            await Clients.Caller.SendAsync("WatchTogetherMessage",
                JsonSerializer.Serialize(new { type = "no-host" }));
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId));
        _rooms.AddMember(roomId, Context.ConnectionId, participantId, role);
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId));
        // Explicit leave: the client already announced it, so forget this connection to avoid a duplicate
        // announcement from OnDisconnectedAsync.
        _rooms.RemoveMember(Context.ConnectionId);
    }

    // json is an already-serialized WatchTogether message (see web watchTogetherTypes.ts).
    public Task Broadcast(string roomId, string json)
    {
        _rooms.Touch(roomId);
        return Clients.OthersInGroup(RoomGroup(roomId)).SendAsync("WatchTogetherMessage", json);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_rooms.RemoveMember(Context.ConnectionId) is { } m)
        {
            // Abrupt disconnect — synthesize the same leave message the client would have sent.
            var json = m.Role == "host"
                ? JsonSerializer.Serialize(new { type = "host-ended", senderId = m.ParticipantId, role = "host" })
                : JsonSerializer.Serialize(new { type = "participant-left", senderId = m.ParticipantId, role = "guest", id = m.ParticipantId });
            await Clients.OthersInGroup(RoomGroup(m.RoomId)).SendAsync("WatchTogetherMessage", json);
        }
        await base.OnDisconnectedAsync(exception);
    }

    // Prefix keeps co-watch rooms from colliding with EventsHub's userId/"global" groups.
    private static string RoomGroup(string roomId) => $"wt:{roomId}";
}
