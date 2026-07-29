using Microsoft.AspNetCore.Http;

namespace Potok.Backend.Infrastructure.Middlewares;

public partial class ModHeaders
{
    private readonly RequestDelegate _next;

    public ModHeaders(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext httpContext)
    {
        httpContext.Response.Headers.AccessControlAllowCredentials = "true";
        httpContext.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        httpContext.Response.Headers.AccessControlAllowHeaders = "Accept, Origin, Content-Type";
        httpContext.Response.Headers.AccessControlAllowMethods = "POST, GET, OPTIONS";

        if (httpContext.Request.Headers.TryGetValue("origin", out var origin))
            httpContext.Response.Headers.AccessControlAllowOrigin = origin.ToString();
        else if (httpContext.Request.Headers.TryGetValue("referer", out var referer))
            httpContext.Response.Headers.AccessControlAllowOrigin = referer.ToString();
        else
            httpContext.Response.Headers.AccessControlAllowOrigin = "*";

        if (HttpMethods.IsOptions(httpContext.Request.Method))
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }

        return _next(httpContext);
    }
}