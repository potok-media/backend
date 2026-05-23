namespace Potok.Backend.Core.Interfaces;

public interface IEventBroadcaster
{
    void Publish<T>(string eventName, T data);
    IAsyncEnumerable<EventEnvelope> Subscribe(Guid clientId, CancellationToken cancellationToken);
    void Unsubscribe(Guid clientId);
}

public record EventEnvelope(string Event, string Data, DateTime Timestamp);
