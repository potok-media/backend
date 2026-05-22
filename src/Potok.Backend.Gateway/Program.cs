using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;
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
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        theme: cleanTheme,
        applyThemeToRedirectedOutput: true)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseSerilog(Log.Logger, dispose: true);

// Add services to the container.
builder.Services.AddSharedInfrastructure(builder.Configuration);
builder.Services.AddGatewayInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

// app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    // For public media, we strip the Authorization header to ensure ResponseCaching can work.
    // ASP.NET Core Response Caching doesn't cache requests with Authorization headers by default.
    if (context.Request.Path.StartsWithSegments("/media/tmdb"))
    {
        context.Request.Headers.Remove("Authorization");
    }
    await next();
});

app.UseResponseCaching();
app.UseAuthorization();

// Ensure DB is created on startup
using (var scope = app.Services.CreateScope())
{
    var settingsRepo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
    await settingsRepo.EnsureDatabaseAsync();
    
    var infuseRepo = scope.ServiceProvider.GetRequiredService<IInfuseRepository>();
    await infuseRepo.EnsureDatabaseAsync();
    
    var torrentRepo = scope.ServiceProvider.GetRequiredService<ITorrentRepository>();
    await torrentRepo.EnsureDatabaseAsync();
}

app.MapControllers();

app.Run();
