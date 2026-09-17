using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Potok.Backend.Infrastructure.Configuration;
using Potok.Backend.Infrastructure.Logging;
using Potok.Backend.Infrastructure.Migrations.Configurations;
using Potok.Backend.SearchEngine;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = SerilogSetup.CreateLogger();

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://*:{port}");
}

builder.Logging.ClearProviders();
builder.Host.UseSerilog(Log.Logger, dispose: true);

// Tracker config (config.yml mounted as config.local.yml in Docker)
builder.Configuration.AddYamlFile("config.local.yml", false, true);


// --- Глобальные настройки ---
CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// --- Регистрация зависимостей ---
builder.Services.AddCoreInfrastructure(builder.Configuration);
builder.Services.AddSearchEngineServices(builder.Configuration);
builder.Services.AddSearchEngineInfrastructure(builder.Configuration);

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
    loggingOptions.MessageTemplate = SerilogSetup.RequestMessageTemplate;
    loggingOptions.GetLevel = SerilogSetup.RequestLogLevel;
    loggingOptions.EnrichDiagnosticContext = SerilogSetup.EnrichRequest;
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
