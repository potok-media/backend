using Serilog.Events;
using Serilog.Parsing;
using Potok.Backend.Infrastructure.Logging;

namespace Potok.Backend.CompositionTests;

public class SerilogSetupTests
{
    [Fact]
    public void QuietHealthCheck_WithoutStatus_IsFiltered()
    {
        var log = Event("/health", status: null);
        Assert.True(SerilogSetup.IsQuietHealthCheck(log));
    }

    [Fact]
    public void QuietHealthCheck_OkHealth_IsFiltered()
    {
        var log = Event("/api/health", 200);
        Assert.True(SerilogSetup.IsQuietHealthCheck(log));
    }

    [Fact]
    public void QuietHealthCheck_ServerError_IsNotFiltered()
    {
        var log = Event("/health", 503);
        Assert.False(SerilogSetup.IsQuietHealthCheck(log));
    }

    [Fact]
    public void QuietHealthCheck_NormalPath_IsNotFiltered()
    {
        var log = Event("/api/v1.0/torrents", 200);
        Assert.False(SerilogSetup.IsQuietHealthCheck(log));
    }

    private static LogEvent Event(string path, int? status)
    {
        var properties = new List<LogEventProperty>
        {
            new("RequestPath", new ScalarValue(path))
        };
        if (status is int code)
            properties.Add(new LogEventProperty("StatusCode", new ScalarValue(code)));

        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("test"),
            properties);
    }
}
