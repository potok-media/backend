using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Gateway.Services;

public class TraktApiHandler : DelegatingHandler
{
    private readonly GatewayOptions _options;

    public TraktApiHandler(IOptions<GatewayOptions> options)
    {
        _options = options.Value;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Add common headers for ALL Trakt requests
        if (!request.Headers.Contains("trakt-api-version"))
            request.Headers.Add("trakt-api-version", "2");
            
        if (!request.Headers.Contains("trakt-api-key"))
            request.Headers.Add("trakt-api-key", _options.TraktClientId);
            
        if (!request.Headers.UserAgent.Any())
            request.Headers.UserAgent.ParseAdd("PotokBFF/1.0");
            
        if (!request.Headers.Accept.Any())
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await base.SendAsync(request, cancellationToken);
    }
}
