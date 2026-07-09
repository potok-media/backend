using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Core.Interfaces.Gateway;

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