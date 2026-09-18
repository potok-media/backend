using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Infrastructure.Http;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.CompositionTests;

public class SearchEngineCompositionTests
{
    [Fact]
    public void SearchEngine_Resolves_CoreServices()
    {
        using var provider = TestServiceCollectionFactory.BuildSearchEngineDomainProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<ISearchService>());
        Assert.NotNull(scope.ServiceProvider.GetService<ITorrentRepository>());
        Assert.NotNull(scope.ServiceProvider.GetService<ISeasonOverrideRepository>());
        Assert.NotNull(scope.ServiceProvider.GetService<IContinueWatchingRepository>());
        Assert.NotNull(scope.ServiceProvider.GetService<TrackerHttpClient>());
        Assert.NotNull(scope.ServiceProvider.GetService<IFlareSolverrClient>());
        Assert.NotNull(scope.ServiceProvider.GetService<CloudflareGuard>());
    }

    [Fact]
    public void SearchEngine_DoesNotRegister_GatewayServices()
    {
        using var provider = TestServiceCollectionFactory.BuildSearchEngineDomainProvider();

        Assert.Null(provider.GetService<IHomeService>());
        Assert.Null(provider.GetService<IMediaOrchestrator>());
        Assert.Null(provider.GetService<IUserRepository>());
        Assert.Null(provider.GetService<IEventBroadcaster>());
    }
}