namespace Potok.Backend.Gateway.Services;

/// <summary>
/// Hardcoded contract between the gateway and its internal plugin-bundler sidecar.
/// These MUST match the constants in the Go bundler (src/Potok.Backend.PluginBundler/main.go).
/// The bundler binds loopback only and is never exposed; the key is defence-in-depth.
/// </summary>
public static class PluginBundlerConstants
{
    public const string HttpClientName = "PluginBundler";
    public const string BaseAddress = "http://127.0.0.1:8787";
    public const string InternalHeader = "X-Potok-Bundler-Key";
    public const string InternalKey = "p0t0k-bundler-internal-7f3a9c2e1b8d4056-do-not-expose";
}
