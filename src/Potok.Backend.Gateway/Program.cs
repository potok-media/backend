using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;
using Potok.Backend.Infrastructure.Migrations.Configurations;
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

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://*:{port}");
}

builder.Logging.ClearProviders();
builder.Host.UseSerilog(Log.Logger, dispose: true);

// Add services to the container.
builder.Services.AddSharedInfrastructure(builder.Configuration);
builder.Services.AddGatewayInfrastructure(builder.Configuration);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(origin => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (httpContext, elapsedMs, ex) =>
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
app.UseCors("AllowAll");

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
app.UseMiddleware<Potok.Backend.Infrastructure.Middlewares.UserContextMiddleware>();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var settingsRepo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
    await settingsRepo.EnsureDatabaseAsync();
    
    var torrentRepo = scope.ServiceProvider.GetRequiredService<ITorrentRepository>();
    await torrentRepo.EnsureDatabaseAsync();
}

app.Services.RunGatewayMigrations();

using (var scope = app.Services.CreateScope())
{
    var gatewayOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
    var adminPassword = !string.IsNullOrEmpty(gatewayOptions.AdminPassword) ? gatewayOptions.AdminPassword : "admin";
    var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    var existingAdmin = await userRepo.GetByUsernameAsync(gatewayOptions.AdminUsername);
    if (existingAdmin == null)
    {
        var adminUser = new Potok.Backend.Core.Entities.User
        {
            Id = Guid.NewGuid(),
            Username = gatewayOptions.AdminUsername,
            PasswordHash = hasher.HashPassword(adminPassword),
            SyncStrategy = "server",
            CreatedAt = DateTime.UtcNow
        };
        await userRepo.CreateAsync(adminUser);
    }
}

app.MapGet("/health", () => Results.Ok());
app.MapControllers();

app.Run();
