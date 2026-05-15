using Potok.Backend.Core.Interfaces;
using Potok.Backend.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSharedInfrastructure();
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
    
    var torrentRepo = scope.ServiceProvider.GetRequiredService<ITorrentRepository>();
    await torrentRepo.EnsureDatabaseAsync();
}

app.MapControllers();

app.Run();
