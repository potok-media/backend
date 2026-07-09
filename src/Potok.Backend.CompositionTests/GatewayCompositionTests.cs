using Microsoft.Extensions.DependencyInjection;
using Potok.Backend.Core.Interfaces.Gateway;

namespace Potok.Backend.CompositionTests;

public class GatewayCompositionTests
{
    [Fact]
    public void Gateway_Resolves_CoreServices()
    {
        using var provider = TestServiceCollectionFactory.BuildGatewayDomainProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IHomeService>());
        Assert.NotNull(scope.ServiceProvider.GetService<IMediaOrchestrator>());
        Assert.NotNull(scope.ServiceProvider.GetService<IUserRepository>());
        Assert.NotNull(scope.ServiceProvider.GetService<ICacheService>());
    }

    [Fact]
    public void Gateway_DoesNotRegister_SearchEngineServices()
    {
        using var provider = TestServiceCollectionFactory.BuildGatewayDomainProvider();

        Assert.Null(provider.GetService<ISearchService>());
        Assert.Null(provider.GetService<ITorrentRepository>());
        Assert.Empty(provider.GetServices<ITrackerSearch>());
    }
}