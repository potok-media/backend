using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Potok.Backend.Core.Entities.Gateway;
using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Infrastructure.Configuration;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IOptions<GatewayOptions> _options;

    public AuthController(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IOptions<GatewayOptions> options)
    {
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _options = options;
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (!_options.Value.MultiUserMode)
        {
            return StatusCode(403, new { error = "REGISTRATION_DISABLED", message = "Registration is disabled on this server." });
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "INVALID_INPUT", message = "Username and password cannot be empty" });
        }

        var existingUser = await _userRepository.GetByUsernameAsync(request.Username);
        if (existingUser != null)
        {
            return BadRequest(new { error = "USER_ALREADY_EXISTS", message = "Username is already taken" });
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username.Trim(),
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            SyncStrategy = "none",
            CreatedAt = DateTime.UtcNow
        };

        await _userRepository.CreateAsync(user);

        var token = _jwtTokenService.GenerateToken(user.Id, user.Username);

        return Ok(new { token, user = UserPayload.From(user, traktConnected: false) });
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "INVALID_INPUT", message = "Username and password cannot be empty" });
        }

        var user = await _userRepository.GetByUsernameAsync(request.Username);
        if (user == null || string.IsNullOrEmpty(user.PasswordHash) || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            return Unauthorized(new { error = "INVALID_CREDENTIALS", message = "Invalid username or password" });
        }

        var token = _jwtTokenService.GenerateToken(user.Id, user.Username);
        var traktToken = await _userRepository.GetTraktTokenAsync(user.Id);

        return Ok(new { token, user = UserPayload.From(user, traktConnected: traktToken != null) });
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
        {
            return Unauthorized(new { error = "UNAUTHORIZED" });
        }

        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { error = "USER_NOT_FOUND" });
        }

        var traktToken = await _userRepository.GetTraktTokenAsync(userId);

        return Ok(UserPayload.From(user, traktConnected: traktToken != null));
    }

    [HttpPost("sync-strategy")]
    [Route("../user/profile/sync-strategy")]
    public async Task<IActionResult> UpdateSyncStrategy([FromBody] UpdateSyncStrategyRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
        {
            return Unauthorized(new { error = "UNAUTHORIZED" });
        }

        if (request.Strategy != "trakt" && request.Strategy != "database" && request.Strategy != "server" && request.Strategy != "local" && request.Strategy != "none")
        {
            return BadRequest(new { error = "INVALID_STRATEGY", message = "Strategy must be 'trakt', 'database', 'server', 'local', or 'none'" });
        }

        await _userRepository.UpdateSyncStrategyAsync(userId, request.Strategy);
        return Ok(new { success = true, strategy = request.Strategy });
    }

    // Change password for accounts that already have one. Telegram-only accounts (no password yet)
    // use set-credentials instead.
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
        {
            return Unauthorized(new { error = "UNAUTHORIZED" });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
        {
            return BadRequest(new { error = "INVALID_INPUT", message = "New password must be at least 6 characters" });
        }

        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { error = "USER_NOT_FOUND" });
        }

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            return BadRequest(new { error = "NO_PASSWORD_SET", message = "This account has no password. Set a login and password first." });
        }

        if (!_passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            return Unauthorized(new { error = "INVALID_CREDENTIALS", message = "Current password is incorrect" });
        }

        await _userRepository.UpdatePasswordAsync(userId, _passwordHasher.HashPassword(request.NewPassword));
        return Ok(new { success = true });
    }

    // Add a username + password to a Telegram-only account (created via Telegram login, no password).
    // After this the account can sign in both ways.
    [HttpPost("set-credentials")]
    public async Task<IActionResult> SetCredentials([FromBody] SetCredentialsRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
        {
            return Unauthorized(new { error = "UNAUTHORIZED" });
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
        {
            return BadRequest(new { error = "INVALID_INPUT", message = "Username cannot be empty and password must be at least 6 characters" });
        }

        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { error = "USER_NOT_FOUND" });
        }

        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            return BadRequest(new { error = "CREDENTIALS_ALREADY_SET", message = "This account already has a password. Use change-password instead." });
        }

        var username = request.Username.Trim();
        var existing = await _userRepository.GetByUsernameAsync(username);
        if (existing != null && existing.Id != userId)
        {
            return BadRequest(new { error = "USER_ALREADY_EXISTS", message = "Username is already taken" });
        }

        await _userRepository.SetCredentialsAsync(userId, username, _passwordHasher.HashPassword(request.Password));

        var updated = await _userRepository.GetByIdAsync(userId);
        var traktToken = await _userRepository.GetTraktTokenAsync(userId);
        return Ok(UserPayload.From(updated!, traktConnected: traktToken != null));
    }
}

public record RegisterRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record UpdateSyncStrategyRequest(string Strategy);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record SetCredentialsRequest(string Username, string Password);
