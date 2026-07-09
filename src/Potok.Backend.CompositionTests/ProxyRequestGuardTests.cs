using Potok.Backend.Gateway.Security;

namespace Potok.Backend.CompositionTests;

public class ProxyRequestGuardTests
{
    [Theory]
    [InlineData("https://example.com/video.m3u8")]
    [InlineData("http://cdn.example.org/segment.ts")]
    public void AllowsPublicHttpTargets(string url)
    {
        var ok = ProxyRequestGuard.TryValidate(url, out var uri, out var error);
        Assert.True(ok);
        Assert.NotNull(uri);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://localhost/secret")]
    [InlineData("http://192.168.1.10/internal")]
    [InlineData("http://10.0.0.5/metadata")]
    [InlineData("http://[::1]/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    public void BlocksUnsafeTargets(string url)
    {
        var ok = ProxyRequestGuard.TryValidate(url, out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}