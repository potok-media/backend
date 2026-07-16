using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Infrastructure.Gateway.Security;

// Long-polls the Telegram Bot API (getUpdates) to receive `/start <code>` messages for the deep-link
// auth flow. This is the only mechanism that works on HTTP / LAN deployments where an inbound webhook
// is not reachable. Registered only when Telegram auth is enabled. The bot must NOT be in webhook
// mode (getUpdates would return 409).
public class TelegramBotPollingService : BackgroundService
{
    private const int LongPollSeconds = 25;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITelegramLinkCodeStore _codeStore;
    private readonly GatewayOptions _options;
    private readonly ILogger<TelegramBotPollingService> _logger;

    public TelegramBotPollingService(
        IHttpClientFactory httpClientFactory,
        ITelegramLinkCodeStore codeStore,
        IOptions<GatewayOptions> options,
        ILogger<TelegramBotPollingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _codeStore = codeStore;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _options.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token)) return;

        var baseUrl = $"https://api.telegram.org/bot{token}";
        long offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                offset = await PollOnceAsync(baseUrl, offset, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram getUpdates poll failed; retrying shortly.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<long> PollOnceAsync(string baseUrl, long offset, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(LongPollSeconds + 10);

        var url = $"{baseUrl}/getUpdates?timeout={LongPollSeconds}&offset={offset}";
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Telegram getUpdates returned {Status}.", (int)response.StatusCode);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return offset;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return offset;
        }

        var newOffset = offset;
        foreach (var update in results.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var updateId) && updateId.TryGetInt64(out var id))
            {
                newOffset = Math.Max(newOffset, id + 1);
            }
            await HandleUpdateAsync(baseUrl, update, ct);
        }
        return newOffset;
    }

    private async Task HandleUpdateAsync(string baseUrl, JsonElement update, CancellationToken ct)
    {
        if (!update.TryGetProperty("message", out var message)) return;
        if (!message.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String) return;

        var text = textEl.GetString() ?? string.Empty;
        if (!text.StartsWith("/start", StringComparison.Ordinal)) return;

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return; // bare /start without a code
        var code = parts[1].Trim();

        if (!message.TryGetProperty("from", out var from) || !from.TryGetProperty("id", out var fromId)
            || !fromId.TryGetInt64(out var telegramId))
        {
            return;
        }

        var username = from.TryGetProperty("username", out var uname) && uname.ValueKind == JsonValueKind.String
            ? uname.GetString()
            : null;

        var chatId = message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var cid)
            && cid.TryGetInt64(out var chatIdVal)
            ? chatIdVal
            : telegramId;

        // Read the entry before confirming so we know the requester's UI language for the reply.
        var entry = _codeStore.Get(code);
        var confirmed = entry != null && _codeStore.Confirm(code, telegramId, username);
        await SendReplyAsync(baseUrl, chatId, ReplyMessage(confirmed, entry?.Language), ct);
    }

    // Bot replies match the requester's UI language (ru/en); the not-found case has no stored
    // language, so it defaults to English.
    private static string ReplyMessage(bool confirmed, string? language)
    {
        var isRu = language != null && language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        if (!confirmed)
        {
            return "⚠️ Code not found or expired. Request a new one in the app.";
        }
        return isRu
            ? "✅ Готово! Вернитесь в приложение — вход подтверждён."
            : "✅ Done! Return to the app — you're signed in.";
    }

    private async Task SendReplyAsync(string baseUrl, long chatId, string message, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var payload = JsonSerializer.Serialize(new { chat_id = chatId, text = message });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await client.PostAsync($"{baseUrl}/sendMessage", content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send Telegram confirmation reply.");
        }
    }
}
