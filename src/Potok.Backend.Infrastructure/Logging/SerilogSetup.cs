using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace Potok.Backend.Infrastructure.Logging;

public static class SerilogSetup
{
    public const string RequestMessageTemplate =
        "Incoming Request: {RequestMethod} {Url} | Status: {StatusCode} | Time: {Elapsed:0}ms";

    private static readonly AnsiConsoleTheme Theme = new(new Dictionary<ConsoleThemeStyle, string>
    {
        [ConsoleThemeStyle.Text] = "\x1b[37m",
        [ConsoleThemeStyle.SecondaryText] = "\x1b[90m",
        [ConsoleThemeStyle.TertiaryText] = "\x1b[90m",
        [ConsoleThemeStyle.String] = "\x1b[32m",
        [ConsoleThemeStyle.Number] = "\x1b[35m",
        [ConsoleThemeStyle.Boolean] = "\x1b[36m",
        [ConsoleThemeStyle.Scalar] = "\x1b[32m",
        [ConsoleThemeStyle.LevelVerbose] = "\x1b[90m",
        [ConsoleThemeStyle.LevelDebug] = "\x1b[90m",
        [ConsoleThemeStyle.LevelInformation] = "\x1b[36m",
        [ConsoleThemeStyle.LevelWarning] = "\x1b[33m",
        [ConsoleThemeStyle.LevelError] = "\x1b[31m",
        [ConsoleThemeStyle.LevelFatal] = "\x1b[31;1m",
        [ConsoleThemeStyle.Name] = "\x1b[37m",
        [ConsoleThemeStyle.Null] = "\x1b[90m",
        [ConsoleThemeStyle.Invalid] = "\x1b[33m"
    });

    public static Serilog.ILogger CreateLogger()
    {
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Filter.ByExcluding(IsQuietHealthCheck)
            .Enrich.FromLogContext()
            .WriteTo.Console(theme: Theme, applyThemeToRedirectedOutput: true)
            .CreateLogger();
    }

    public static LogEventLevel RequestLogLevel(HttpContext httpContext, double elapsedMs, Exception? ex)
    {
        if (ex != null)
            return LogEventLevel.Error;

        if (IsHealthPath(httpContext.Request.Path))
        {
            return httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : LogEventLevel.Verbose;
        }

        return LogEventLevel.Information;
    }

    public static void EnrichRequest(IDiagnosticContext diagnosticContext, HttpContext httpContext)
    {
        diagnosticContext.Set("Url", httpContext.Request.GetDisplayUrl());
    }

    internal static bool IsQuietHealthCheck(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("RequestPath", out var path))
            return false;

        if (!path.ToString().Contains("health", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!logEvent.Properties.TryGetValue("StatusCode", out var status))
            return true;

        return status is ScalarValue { Value: int code } && code < 500;
    }

    private static bool IsHealthPath(PathString path)
    {
        return path.StartsWithSegments("/health") || path.StartsWithSegments("/api/health");
    }
}
