using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Middlewares;

public class UserContextMiddleware
{
    private readonly RequestDelegate _next;

    public UserContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IJwtTokenService jwtTokenService, IUserRepository userRepository, IOptions<GatewayOptions> options)
    {
        var gatewayOptions = options.Value;
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        if (path.StartsWith("/api/handshake") || 
            path.StartsWith("/api/auth/login") || 
            path.StartsWith("/api/health") || 
            path.StartsWith("/health") || 
            path.StartsWith("/api/events") || 
            path.StartsWith("/media/tmdb") || 
            path.StartsWith("/api/media") || 
            path.StartsWith("/api/proxy") || 
            (path.StartsWith("/api/auth/register") && gatewayOptions.MultiUserMode))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers["Authorization"].ToString();
        ClaimsPrincipal? authenticatedUser = null;

        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader.Substring("Bearer ".Length).Trim();
            var validatedUser = jwtTokenService.ValidateToken(token);
            if (validatedUser != null)
            {
                var userIdStr = validatedUser.FindFirstValue(ClaimTypes.NameIdentifier) ?? validatedUser.FindFirstValue("sub");
                if (Guid.TryParse(userIdStr, out var userId))
                {
                    var existingUser = await userRepository.GetByIdAsync(userId);
                    if (existingUser != null)
                    {
                        authenticatedUser = validatedUser;
                        context.User = authenticatedUser;
                    }
                }
            }
        }

        if (gatewayOptions.AuthRequired)
        {
            if (authenticatedUser == null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED", message = "Доступ к серверу ограничен." });
                return;
            }
        }
        else
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                var defaultUserId = Guid.NewGuid().ToString();
                var identity = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, defaultUserId),
                    new Claim(ClaimTypes.Name, "default-user")
                }, "BypassAuth");
                context.User = new ClaimsPrincipal(identity);
            }
        }

        await _next(context);
    }
}
