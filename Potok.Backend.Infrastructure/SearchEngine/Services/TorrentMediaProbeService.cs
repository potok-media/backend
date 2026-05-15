using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces;
using Potok.Backend.Core.Models.Options;
using Potok.Backend.Core.Models.Tracks;
using Potok.Backend.Core.Utils;

namespace Potok.Backend.Infrastructure.SearchEngine.Services;

public sealed class TorrentMediaProbeService : ITorrentMediaProbeService
{
    private readonly Config _config;
    private readonly HttpService _httpService;
    private readonly ILogger<TorrentMediaProbeService> _logger;
    private readonly ITorrentRepository _torrentRepository;

    public TorrentMediaProbeService(
        ITorrentRepository torrentRepository,
        HttpService httpService,
        ILogger<TorrentMediaProbeService> logger,
        IOptionsSnapshot<Config> config)
    {
        _torrentRepository = torrentRepository;
        _httpService = httpService;
        _logger = logger;
        _config = config.Value;
    }

    public async Task ExecuteAsync()
    {
        if (!_config.Ffprobe.Enable || _config.Ffprobe.TsUri is null)
            return;

        var torrents = await _torrentRepository.GetForMediaProbeAsync(
            _config.Ffprobe.BatchSize,
            _config.Ffprobe.Attempts);

        if (torrents.Count == 0)
            return;
        
        var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = cancellationTokenSource.Token;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(torrents, options, async (torrent, _) =>
        {
            if (string.IsNullOrWhiteSpace(torrent.Magnet))
            {
                await _torrentRepository.IncrementMediaProbeAttemptsAsync(torrent.Url);
                return;
            }

            try
            {
                var response = await RunFfprobeAsync(torrent.Magnet, cancellationToken);
                var streams = response?.Streams;
                if (streams == null || streams.Count == 0)
                {
                    await _torrentRepository.IncrementMediaProbeAttemptsAsync(torrent.Url);
                    return;
                }

                NormalizeStreamTitles(streams);
                var languages = ExtractLanguagesFromFfprobe(streams);
                await _torrentRepository.UpdateMediaProbeAsync(torrent.Url, streams, languages);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to probe torrent {Url}", torrent.Url);
                await _torrentRepository.IncrementMediaProbeAttemptsAsync(torrent.Url);
            }
        });
    }

    private void NormalizeStreamTitles(List<FfStream> streams)
    {
        foreach (var stream in streams)
        {
            if (stream.Tags?.Title == null) continue;

            if (stream.Tags.Title.Any(c => c >= 0x2500 && c <= 0x257F))
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    var cp866 = Encoding.GetEncoding("cp866");
                    var bytes = cp866.GetBytes(stream.Tags.Title);
                    var decoded = Encoding.UTF8.GetString(bytes);

                    if (decoded.Any(c => c >= 'а' && c <= 'я'))
                        stream.Tags.Title = decoded;
                }
                catch
                {
                    // ignored
                }
        }
    }

    private async Task<FfprobeResponse?> RunFfprobeAsync(string magnet, CancellationToken cancellationToken)
    {
        var uri = _config.Ffprobe.TsUri;
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var baseUri = uri.TrimEnd('/');
        var streamUrl = $"{baseUri}/stream?link={Uri.EscapeDataString(magnet)}&index=1&play";

        var authHeader = BuildAuthHeader(_config.Ffprobe.Authorization.Login, _config.Ffprobe.Authorization.Password);
        var headers = string.IsNullOrEmpty(authHeader)
            ? null
            : new Dictionary<string, string> { { "Authorization", authHeader } };

        var requestOptions = new RequestOptions
        {
            TimeoutSeconds = 180,
            Headers = headers,
            CancellationToken = cancellationToken,
            UseProxy = false
        };

        using var response = await _httpService.GetResponseAsync(streamUrl, requestOptions);

        if (!response.IsSuccessStatusCode)
            return null;

        Process? process = null;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = "-v quiet -print_format json -show_format -show_streams -i pipe:0",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    if (process.HasExited)
                        break;

                    try
                    {
                        await process.StandardInput.BaseStream.WriteAsync(buffer, 0, read, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch
                {
                    // ignored
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    _logger.LogDebug("ffprobe failed: {Error}", error);
                return null;
            }

            return JsonSerializer.Deserialize<FfprobeResponse>(
                output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffprobe crashed");
            return null;
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                    process.Kill(true);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static string? BuildAuthHeader(string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(user) || password == null)
            return null;

        var raw = $"{user}:{password}";
        return $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))}";
    }

    private static HashSet<string>? ExtractLanguagesFromFfprobe(List<FfStream>? streams)
    {
        if (streams == null || streams.Count == 0)
            return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in streams)
        {
            if (!string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
                continue;

            var lang = stream.Tags?.Language;
            if (!string.IsNullOrWhiteSpace(lang))
                set.Add(lang);
        }

        return set.Count > 0 ? set : null;
    }
}