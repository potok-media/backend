using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.Gateway;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/auth/telegram")]
public class TelegramAuthController : ControllerBase
{
    // Nullable: when Telegram auth is disabled these services are never registered (see
    // ServicesConfiguration), so every endpoint here reports the feature as unavailable.
    private readonly ITelegramAuthService? _telegramAuthService;
    private readonly ITelegramLinkCodeStore? _linkCodeStore;
    private readonly IUserRepository _userRepository;
    private readonly IOptions<GatewayOptions> _options;

    public TelegramAuthController(
        IUserRepository userRepository,
        IOptions<GatewayOptions> options,
        ITelegramAuthService? telegramAuthService = null,
        ITelegramLinkCodeStore? linkCodeStore = null)
    {
        _userRepository = userRepository;
        _options = options;
        _telegramAuthService = telegramAuthService;
        _linkCodeStore = linkCodeStore;
    }

    [AllowAnonymous]
    [HttpPost("")]
    public async Task<IActionResult> Authenticate([FromBody] Dictionary<string, JsonElement> data)
    {
        if (_telegramAuthService == null) return TelegramDisabled();

        var result = await _telegramAuthService.AuthenticateAsync(ToStringMap(data));
        if (!result.Success) return MapError(result.ErrorCode);

        return Ok(new { token = result.Token, user = UserPayload.From(result.User!, result.TraktConnected) });
    }

    [HttpPost("link")]
    public async Task<IActionResult> Link([FromBody] Dictionary<string, JsonElement> data)
    {
        if (_telegramAuthService == null) return TelegramDisabled();
        if (!TryGetUserId(out var userId)) return Unauthorized(new { error = "UNAUTHORIZED" });

        var result = await _telegramAuthService.LinkAsync(userId, ToStringMap(data));
        if (!result.Success) return MapError(result.ErrorCode);

        return Ok(UserPayload.From(result.User!, result.TraktConnected));
    }

    [HttpPost("unlink")]
    public async Task<IActionResult> Unlink()
    {
        if (_telegramAuthService == null) return TelegramDisabled();
        if (!TryGetUserId(out var userId)) return Unauthorized(new { error = "UNAUTHORIZED" });

        await _telegramAuthService.UnlinkAsync(userId);

        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null) return NotFound(new { error = "USER_NOT_FOUND" });
        var traktToken = await _userRepository.GetTraktTokenAsync(userId);
        return Ok(UserPayload.From(user, traktToken != null));
    }

    // Deep-link (bot) flow, used on HTTP deployments where the widget can't run. Issues a one-time
    // code and a t.me deep link. Anonymous request => login/register intent; authenticated (bearer
    // present) => link-to-current-account intent.
    [AllowAnonymous]
    [HttpPost("start-code")]
    public IActionResult StartCode([FromBody] TelegramStartCodeRequest? request = null)
    {
        if (_telegramAuthService == null || _linkCodeStore == null) return TelegramDisabled();

        var purpose = TryGetUserId(out var userId) ? "link" : "auth";
        var language = string.IsNullOrWhiteSpace(request?.Language) ? "en" : request!.Language!;
        var entry = _linkCodeStore.Create(purpose, purpose == "link" ? userId : null, language);
        var botUsername = _options.Value.TelegramBotUsername;

        return Ok(new
        {
            code = entry.Code,
            botUsername,
            deepLink = $"https://t.me/{botUsername}?start={entry.Code}"
        });
    }

    // Polled by the frontend until the bot confirms the code. Returns pending/expired, or on
    // confirmation resolves the Telegram identity exactly like the widget flow.
    [AllowAnonymous]
    [HttpPost("poll")]
    public async Task<IActionResult> Poll([FromBody] TelegramPollRequest request)
    {
        if (_telegramAuthService == null || _linkCodeStore == null) return TelegramDisabled();
        if (string.IsNullOrEmpty(request.Code)) return BadRequest(new { error = "INVALID_INPUT" });

        var entry = _linkCodeStore.Get(request.Code);
        if (entry == null) return Ok(new { status = "expired" });
        if (!entry.Confirmed) return Ok(new { status = "pending" });

        _linkCodeStore.Remove(entry.Code);

        var result = entry.Purpose == "link" && entry.UserId is { } linkUserId
            ? await _telegramAuthService.LinkByTelegramIdAsync(linkUserId, entry.TelegramId, entry.TelegramUsername)
            : await _telegramAuthService.AuthenticateByTelegramIdAsync(entry.TelegramId, entry.TelegramUsername);

        if (!result.Success) return MapError(result.ErrorCode);

        return Ok(new { status = "confirmed", token = result.Token, user = UserPayload.From(result.User!, result.TraktConnected) });
    }

    private bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrEmpty(userIdStr) && Guid.TryParse(userIdStr, out userId);
    }

    private IActionResult TelegramDisabled() =>
        StatusCode(404, new { error = "TELEGRAM_AUTH_DISABLED", message = "Telegram authentication is not enabled on this server." });

    private IActionResult MapError(string? errorCode) => errorCode switch
    {
        "INVALID_TELEGRAM_HASH" => Unauthorized(new { error = errorCode, message = "Telegram authorization could not be verified." }),
        "REGISTRATION_DISABLED" => StatusCode(403, new { error = errorCode, message = "Registration is disabled on this server." }),
        "TELEGRAM_ALREADY_LINKED" => BadRequest(new { error = errorCode, message = "This Telegram account is already linked to another user." }),
        _ => BadRequest(new { error = errorCode ?? "TELEGRAM_AUTH_FAILED" })
    };

    // The data-check-string must include every field Telegram sent, using its exact string form.
    // JSON strings are unquoted via GetString(); numbers (id, auth_date) keep their raw digits.
    private static Dictionary<string, string> ToStringMap(Dictionary<string, JsonElement> data)
    {
        var map = new Dictionary<string, string>(data.Count);
        foreach (var (key, value) in data)
        {
            map[key] = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        }
        return map;
    }
}

public record TelegramPollRequest(string Code);
public record TelegramStartCodeRequest(string? Language);
