using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly IEventBroadcaster _broadcaster;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EventsController(IEventBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    [HttpGet]
    public async Task Get(CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid();
        var traceId = Guid.NewGuid().ToString();
        var subscription = _broadcaster.Subscribe(clientId, cancellationToken);

        // 1. Task to stream broadcaster events to the client
        var sendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in subscription.WithCancellation(cancellationToken))
                {
                    var frame = new
                    {
                        @event = envelope.Event,
                        payload = envelope.Data,
                        timestamp = DateTime.UtcNow.ToString("o"),
                        version = "1.0",
                        traceId = traceId
                    };
                    await SafeSendFrameAsync(webSocket, frame, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error in WebSocket send loop for client {ClientId}", clientId);
            }
        }, cancellationToken);

        // 2. Main thread receive loop (waits for client close signals)
        var buffer = new byte[1024 * 4];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Socket drop or cancel
        }
        finally
        {
            // Unsubscribe client from the gateway broadcaster
            _broadcaster.Unsubscribe(clientId);

            // Wait for streaming task to finish gracefully
            try
            {
                await sendTask;
            }
            catch
            {
                // Suppress background task completion errors on disconnect
            }

            // Cleanly close the socket from the server side if still open
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }

    private async Task SafeSendFrameAsync(WebSocket webSocket, object frame, CancellationToken cancellationToken)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(
                    new ArraySegment<byte>(jsonBytes),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken
                );
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}



