using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Infrastructure.Configuration;
using Potok.Backend.Infrastructure.Migrations.Configurations;
using Potok.Backend.SearchEngine;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

var cleanTheme = new AnsiConsoleTheme(new Dictionary<ConsoleThemeStyle, string>
{
    // основной текст
    [ConsoleThemeStyle.Text] = "\x1b[37m",

    // второстепенное
    [ConsoleThemeStyle.SecondaryText] = "\x1b[90m",
    [ConsoleThemeStyle.TertiaryText] = "\x1b[90m",

    // данные
    [ConsoleThemeStyle.String] = "\x1b[32m", // мягкий зелёный
    [ConsoleThemeStyle.Number] = "\x1b[35m", // фиолетовый
    [ConsoleThemeStyle.Boolean] = "\x1b[36m", // бирюзовый
    [ConsoleThemeStyle.Scalar] = "\x1b[32m",

    // уровни
    [ConsoleThemeStyle.LevelVerbose] = "\x1b[90m",
    [ConsoleThemeStyle.LevelDebug] = "\x1b[90m",

    [ConsoleThemeStyle.LevelInformation] = "\x1b[36m", // спокойный cyan
    [ConsoleThemeStyle.LevelWarning] = "\x1b[33m", // amber
    [ConsoleThemeStyle.LevelError] = "\x1b[31m", // red
    [ConsoleThemeStyle.LevelFatal] = "\x1b[31;1m", // яркий красный только для fatal

    // прочее
    [ConsoleThemeStyle.Name] = "\x1b[37m",
    [ConsoleThemeStyle.Null] = "\x1b[90m",
    [ConsoleThemeStyle.Invalid] = "\x1b[33m"
});

Log.Logger = new LoggerConfiguration()
    //.MinimumLevel.Information()
    //.MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    //.MinimumLevel.Override("System", LogEventLevel.Warning)
    .Filter.ByExcluding(logEvent => 
        logEvent.Properties.TryGetValue("RequestPath", out var path) && 
        path.ToString().Contains("health") &&
        logEvent.Properties.TryGetValue("StatusCode", out var status) &&
        status is ScalarValue scalar && 
        scalar.Value is int code && 
        code < 500)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        theme: cleanTheme,
        applyThemeToRedirectedOutput: true)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog(Log.Logger, dispose: true);

// 1. Добавляем файл в общую конфигурацию приложения
builder.Configuration.AddYamlFile("config.local.yml", false, true);

// 2. Настраиваем Kestrel


// --- Глобальные настройки ---
CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// --- Регистрация зависимостей ---
builder.Services.AddSharedInfrastructure(builder.Configuration);
builder.Services.AddSearchEngineInfrastructure(builder.Configuration);
builder.Services.AddSignalR();

var app = builder.Build();

// --- Middleware ---
app.MapOpenApi();
app.MapScalarApiReference();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(new
        {
            error = "Internal server error",
            message = "An unexpected error occurred. Please try again later."
        }.ToJson());
    });
});

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseRouting();
app.UseResponseCompression();

app.UseModHeaders();

app.UseSerilogRequestLogging(loggingOptions =>
{
    loggingOptions.MessageTemplate =
        "Incoming Request: {RequestMethod} {Url} | Status: {StatusCode} | Time: {Elapsed:0}ms";

    loggingOptions.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        var fullUrl = httpContext.Request.GetDisplayUrl();

        diagnosticContext.Set("Url", fullUrl);
    };

    loggingOptions.GetLevel = (httpContext, elapsedMs, ex) =>
    {
        if (ex != null) return Serilog.Events.LogEventLevel.Error;
        if (httpContext.Request.Path.StartsWithSegments("/health") || 
            httpContext.Request.Path.StartsWithSegments("/api/health"))
        {
            return httpContext.Response.StatusCode >= 500 
                ? Serilog.Events.LogEventLevel.Error 
                : Serilog.Events.LogEventLevel.Verbose;
        }
        return Serilog.Events.LogEventLevel.Information;
    };
});

app.MapControllers();

// --- Миграция БД ---
app.Services.RunSearchEngineMigrations();

// --- Запуск приложения ---
await app.RunAsync();

// --- Вспомогательные методы ---
namespace Potok.Backend.SearchEngine
{
    internal static class Extensions
    {
        public static string ToJson(this object obj)
        {
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                PropertyNamingPolicy = null
            });
        }
    }
}
