using Microsoft.AspNetCore.Builder;
using ModHeaders = Potok.Backend.Infrastructure.Middlewares.ModHeaders;

namespace Potok.Backend.Infrastructure.Configuration;

public static class Extensions
{
    public static IApplicationBuilder UseModHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ModHeaders>();
    }
}