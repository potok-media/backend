using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Potok.Backend.Infrastructure.Gateway.Hubs;

// Co-watching ("watch together") sync hub. A host mints a random room key (base64url on the client) and
// shares a link; guests JoinRoom the same key. The host is the sole authority and broadcasts opaque JSON
// messages (timing/pause/control/lifecycle) to the room via OthersInGroup (sender gets no echo).
//
// Presence: JoinRoom records connectionId → {room, participantId, role} so that an ABRUPT disconnect (tab
// close / network drop, i.e. no explicit LeaveRoom) is announced to the room as a leave — that's the
// server-side backstop for the client-authored participant-left/host-ended, which only fires on an explicit
// Leave. In-memory (single gateway instance); a multi-instance deployment would need a Redis backplane.
//
// Note: string hub-method parameters bind from the client only because implicit from-services binding is
// disabled in Program.cs (a connection string is registered as a singleton `string`).
public class WatchTogetherHub : Hub
{
    private static readonly ConcurrentDictionary<string, RoomMember> Members = new();

    private sealed record RoomMember(string RoomId, string ParticipantId, string Role);

    public async Task JoinRoom(string roomId, string participantId, string role)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId));
        Members[Context.ConnectionId] = new RoomMember(roomId, participantId, role);
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId));
        // Explicit leave: the client already announced it, so forget this connection to avoid a duplicate
        // announcement from OnDisconnectedAsync.
        Members.TryRemove(Context.ConnectionId, out _);
    }

    // json is an already-serialized WatchTogether message (see web watchTogetherTypes.ts).
    public Task Broadcast(string roomId, string json) =>
        Clients.OthersInGroup(RoomGroup(roomId)).SendAsync("WatchTogetherMessage", json);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Members.TryRemove(Context.ConnectionId, out var m))
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
