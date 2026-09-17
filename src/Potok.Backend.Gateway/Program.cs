using Potok.Backend.Infrastructure.Configuration;
using Potok.Backend.Infrastructure.Logging;
using Potok.Backend.Infrastructure.Migrations.Configurations;
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

builder.Services.AddCoreInfrastructure(builder.Configuration);
builder.Services.AddGatewayServices(builder.Configuration);
builder.Services.AddGatewayInfrastructure(builder.Configuration);
builder.Services.AddSignalR(options =>
{
   options.DisableImplicitFromServicesParameters = true;
});

// Co-watch room registry (presence + lifecycle) + its idle-room sweeper. In-memory, single instance; behind
// IRoomStore so a Redis-backed store can drop in for multi-instance / restart-durable deployments.
builder.Services.AddSingleton<Potok.Backend.Infrastructure.Gateway.Hubs.IRoomStore,
    Potok.Backend.Infrastructure.Gateway.Hubs.RoomStore>();
builder.Services.AddHostedService<Potok.Backend.Infrastructure.Gateway.Hubs.RoomSweeper>();

// Internal plugin-bundler sidecar: supervised child process + loopback-only client.
// Hidden, hardcoded, never exposed — see Services/PluginBundlerConstants.cs.
builder.Services.AddHostedService<Potok.Backend.Gateway.Services.PluginBundlerHost>();
builder.Services.AddHttpClient(Potok.Backend.Gateway.Services.PluginBundlerConstants.HttpClientName, client =>
{
    client.BaseAddress = new Uri(Potok.Backend.Gateway.Services.PluginBundlerConstants.BaseAddress);
    client.Timeout = TimeSpan.FromSeconds(30);
});

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

var forwardedHeadersOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseExceptionHandler();
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = SerilogSetup.RequestMessageTemplate;
    options.GetLevel = SerilogSetup.RequestLogLevel;
    options.EnrichDiagnosticContext = SerilogSetup.EnrichRequest;
});
app.UseCors("AllowAll");
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/media/tmdb"))
    {
        context.Request.Headers.Remove("Authorization");
    }
    await next();
});

app.UseResponseCaching();
app.UseRouting();
app.UseMiddleware<Potok.Backend.Infrastructure.Middlewares.UserContextMiddleware>();
app.UseAuthorization();

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
        var adminUser = new User
        {
            Id = Guid.NewGuid(),
            Username = gatewayOptions.AdminUsername,
            PasswordHash = hasher.HashPassword(adminPassword),
            SyncStrategy = "none",
            CreatedAt = DateTime.UtcNow
        };
        await userRepo.CreateAsync(adminUser);
    }
}

app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
app.MapControllers();
app.MapHub<Potok.Backend.Infrastructure.Gateway.Hubs.EventsHub>("/api/events").AllowAnonymous();
app.MapHub<Potok.Backend.Infrastructure.Gateway.Hubs.WatchTogetherHub>("/api/watch-together").AllowAnonymous();

app.Run();
