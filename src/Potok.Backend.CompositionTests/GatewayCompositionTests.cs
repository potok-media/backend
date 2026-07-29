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

    // The SearchEngine domain no longer exists in this repo — its types aren't referenced at all,
    // which is a stronger guarantee than the old negative-registration assertion.
}
