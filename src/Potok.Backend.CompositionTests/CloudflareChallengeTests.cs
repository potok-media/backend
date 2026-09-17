using System.Net;
using Potok.Backend.Infrastructure.Http.FlareSolverr;

namespace Potok.Backend.CompositionTests;

public class CloudflareChallengeTests
{
    [Fact]
    public void IsChallenge_ForbiddenWithCfMitigated_IsTrue()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");

        Assert.True(CloudflareChallenge.IsChallenge(response));
    }

    [Fact]
    public void IsChallenge_UnavailableWithCfMitigated_IsTrue()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");

        Assert.True(CloudflareChallenge.IsChallenge(response));
    }

    [Fact]
    public void IsChallenge_OkWithCfRay_IsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("cf-ray", "abc");

        Assert.False(CloudflareChallenge.IsChallenge(response));
    }

    [Fact]
    public void IsChallenge_ForbiddenWithoutMitigated_IsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("cf-ray", "abc");

        Assert.False(CloudflareChallenge.IsChallenge(response));
    }

    [Fact]
    public void IsChallenge_Null_IsFalse()
    {
        Assert.False(CloudflareChallenge.IsChallenge(null));
    }

    [Theory]
    [InlineData("Just a moment...")]
    [InlineData("Один момент, пожалуйста")]
    [InlineData("var cf_chl_opt = {}")]
    [InlineData("cf-browser-verification")]
    [InlineData("/cdn-cgi/challenge-platform/h/b")]
    public void IsChallengeBody_KnownMarkers_IsTrue(string body)
    {
        Assert.True(CloudflareChallenge.IsChallengeBody(body));
    }

    [Fact]
    public void IsChallengeBody_EmptyOrNull_IsFalse()
    {
        Assert.False(CloudflareChallenge.IsChallengeBody(null));
        Assert.False(CloudflareChallenge.IsChallengeBody(""));
        Assert.False(CloudflareChallenge.IsChallengeBody("normal tracker html"));
    }

    [Fact]
    public void IsChallengeBody_TooLong_IsFalse()
    {
        var body = "Just a moment" + new string('x', 200_001);
        Assert.False(CloudflareChallenge.IsChallengeBody(body));
    }
}
