using System.Collections.Concurrent;
using System.Threading.Channels;
using Potok.Backend.Core.Interfaces;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class EventBroadcaster : IEventBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<EventEnvelope>> _subscribers = new();

    public void Publish<T>(string eventName, T data)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var envelope = new EventEnvelope(eventName, json, DateTime.UtcNow);

        foreach (var channel in _subscribers.Values)
        {
            // Non-blocking try write to each subscriber's channel
            channel.Writer.TryWrite(envelope);
        }
    }

    public IAsyncEnumerable<EventEnvelope> Subscribe(Guid clientId, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _subscribers.TryAdd(clientId, channel);
        cancellationToken.Register(() => Unsubscribe(clientId));

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public void Unsubscribe(Guid clientId)
    {
        if (_subscribers.TryRemove(clientId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}
