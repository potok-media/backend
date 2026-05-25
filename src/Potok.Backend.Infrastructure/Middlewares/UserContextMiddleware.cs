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

    public async Task InvokeAsync(HttpContext context, IJwtTokenService jwtTokenService, IOptions<GatewayOptions> options)
    {
        var gatewayOptions = options.Value;

        if (!gatewayOptions.MultiUserMode)
        {
            // --- Personal Mode Bypass ---
            // Automatically inject the default-user identity on every request
            var defaultUserId = "00000000-0000-0000-0000-000000000000";
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, defaultUserId),
                new Claim(ClaimTypes.Name, "default-user")
            }, "BypassAuth");

            context.User = new ClaimsPrincipal(identity);
        }
        else
        {
            // --- Shared Mode JWT Validation ---
            var authHeader = context.Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring("Bearer ".Length).Trim();
                var principal = jwtTokenService.ValidateToken(token);
                if (principal != null)
                {
                    context.User = principal;
                }
            }
        }

        await _next(context);
    }
}
