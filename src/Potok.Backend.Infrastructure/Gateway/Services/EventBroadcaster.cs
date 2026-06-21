using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Gateway.Hubs;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class EventBroadcaster : IEventBroadcaster
{
    private readonly IHubContext<EventsHub> _hubContext;

    public EventBroadcaster(IHubContext<EventsHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public void Publish<T>(string eventName, T data, Guid? userId = null)
    {
        var frame = new
        {
            @event = eventName,
            payload = data,
            timestamp = DateTime.UtcNow.ToString("o"),
            version = "1.0",
            traceId = Guid.NewGuid().ToString()
        };

        if (userId.HasValue)
        {
            var userKey = userId.Value.ToString();
            // Send specifically to the group for this user
            _hubContext.Clients.Group(userKey).SendAsync("ReceiveEvent", frame)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Serilog.Log.Error(t.Exception, "Error broadcasting event {Event} to user {UserId}", eventName, userId);
                    }
                }, TaskScheduler.Default);
        }
        else
        {
            // Send to the global group (which includes both anonymous and authenticated connections)
            _hubContext.Clients.Group("global").SendAsync("ReceiveEvent", frame)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Serilog.Log.Error(t.Exception, "Error broadcasting event {Event} globally", eventName);
                    }
                }, TaskScheduler.Default);
        }
    }
}
