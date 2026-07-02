using System.Diagnostics;

namespace Potok.Backend.Gateway.Services;

/// <summary>
/// Launches and supervises the internal plugin-bundler — a tiny Go sidecar baked
/// into this image. It binds loopback only and is never exposed; the gateway is
/// its sole client. The process is started on boot, restarted with backoff if it
/// crashes, and killed on shutdown. No configuration: everything is hardcoded.
/// </summary>
public sealed class PluginBundlerHost : BackgroundService
{
    private readonly ILogger<PluginBundlerHost> _logger;

    // The Go build stage in the gateway Dockerfile drops the binary next to the
    // published app (i.e. AppContext.BaseDirectory == /app in the container).
    private static readonly string BinaryPath =
        Path.Combine(AppContext.BaseDirectory, "potok-plugin-bundler");

    public PluginBundlerHost(ILogger<PluginBundlerHost> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!File.Exists(BinaryPath))
        {
            _logger.LogWarning(
                "Plugin bundler binary not found at {Path}; plugin bundling disabled for this run.",
                BinaryPath);
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                process = StartProcess();
                _logger.LogInformation("Plugin bundler started (pid {Pid})", process.Id);
                backoff = TimeSpan.FromSeconds(1); // reset after a clean start

                await process.WaitForExitAsync(stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                    _logger.LogWarning("Plugin bundler exited (code {Code}); restarting.", process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                // Gateway is shutting down — fall through to kill + exit.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin bundler supervision error.");
            }
            finally
            {
                if (process is { HasExited: false })
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill plugin bundler."); }
                }
                process?.Dispose();
            }

            if (stoppingToken.IsCancellationRequested) break;

            try { await Task.Delay(backoff, stoppingToken); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private Process StartProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = BinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) _logger.LogInformation("[bundler] {Line}", e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _logger.LogInformation("[bundler] {Line}", e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
}
