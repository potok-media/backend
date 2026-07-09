using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Potok.Backend.Core.Interfaces.Gateway;

namespace Potok.Backend.Infrastructure.Gateway.Hubs;

public class EventsHub : Hub
{
    private readonly IJwtTokenService _jwtTokenService;

    public EventsHub(IJwtTokenService jwtTokenService)
    {
        _jwtTokenService = jwtTokenService;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        Guid? userId = null;

        if (httpContext != null)
        {
            string? token = null;
            var authHeader = httpContext.Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader["Bearer ".Length..].Trim();
            }
            else
            {
                token = httpContext.Request.Query["token"];
                if (string.IsNullOrEmpty(token))
                {
                    token = httpContext.Request.Query["access_token"];
                }
            }

            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    var principal = _jwtTokenService.ValidateToken(token);
                    if (principal != null)
                    {
                        var userIdStr = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                                        ?? principal.FindFirst("sub")?.Value;
                        if (!string.IsNullOrEmpty(userIdStr) && Guid.TryParse(userIdStr, out var parsedUserId))
                        {
                            userId = parsedUserId;
                        }
                    }
                }
                catch
                {
                    // Ignore token validation failure and fallback to anonymous
                }
            }
        }

        // Add client to their user group
        string userGroup = userId.HasValue ? userId.Value.ToString() : "global";
        await Groups.AddToGroupAsync(Context.ConnectionId, userGroup);

        // Also add authenticated users to the "global" group so they receive global broadcasts
        if (userId.HasValue)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "global");
        }

        await base.OnConnectedAsync();
    }
}
