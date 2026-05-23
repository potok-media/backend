using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly IEventBroadcaster _broadcaster;

    public EventsController(IEventBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    [HttpGet]
    public async Task Get(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        await Response.Body.FlushAsync(cancellationToken);

        var clientId = Guid.NewGuid();
        var subscription = _broadcaster.Subscribe(clientId, cancellationToken);
        
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var enumerator = subscription.GetAsyncEnumerator(cancellationToken);
        Task<bool>? eventTask = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                eventTask ??= enumerator.MoveNextAsync().AsTask();
                var timerTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();

                var completedTask = await Task.WhenAny(eventTask, timerTask);

                if (completedTask == eventTask)
                {
                    if (!await eventTask) break;
                    
                    var envelope = enumerator.Current;
                    await Response.BodyWriter.WriteAsync(
                        System.Text.Encoding.UTF8.GetBytes($"event: {envelope.Event}\ndata: {envelope.Data}\n\n"), 
                        cancellationToken
                    );
                    await Response.Body.FlushAsync(cancellationToken);
                    
                    eventTask = null;
                }
                else
                {
                    // Keep-Alive comment to prevent proxy timeouts
                    await Response.BodyWriter.WriteAsync(
                        System.Text.Encoding.UTF8.GetBytes(":\n\n"), 
                        cancellationToken
                    );
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit when client disconnects
        }
        finally
        {
            _broadcaster.Unsubscribe(clientId);
            await enumerator.DisposeAsync();
        }
    }
}
