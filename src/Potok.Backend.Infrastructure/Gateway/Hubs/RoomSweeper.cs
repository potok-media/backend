using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Potok.Backend.Infrastructure.Gateway.Hubs;

// Periodic safety net for the room registry. RemoveMember already drops rooms the moment they empty out (and
// SignalR's own keep-alive fires OnDisconnectedAsync for dead sockets), so this only mops up leaked entries via
// a generous idle ceiling — it never touches a room with live members before the ceiling.
public sealed class RoomSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdleTtl = TimeSpan.FromHours(24);

    private readonly IRoomStore _rooms;
    private readonly ILogger<RoomSweeper> _logger;

    public RoomSweeper(IRoomStore rooms, ILogger<RoomSweeper> logger)
    {
        _rooms = rooms;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var reaped = _rooms.Reap(IdleTtl);
                if (reaped.Count > 0)
                    _logger.LogInformation("[WatchTogether] Reaped {Count} stale room(s).", reaped.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WatchTogether] Room sweep failed.");
            }
        }
    }
}
