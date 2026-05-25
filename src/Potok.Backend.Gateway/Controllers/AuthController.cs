using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Entities;
using Potok.Backend.Core.Interfaces;

namespace Potok.Backend.Gateway.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;

    public AuthController(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService)
    {
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
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
            SyncStrategy = "database",
            CreatedAt = DateTime.UtcNow
        };

        await _userRepository.CreateAsync(user);

        var token = _jwtTokenService.GenerateToken(user.Id, user.Username);

        return Ok(new
        {
            token,
            user = new
            {
                id = user.Id,
                username = user.Username,
                syncStrategy = user.SyncStrategy
            }
        });
    }

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

        return Ok(new
        {
            token,
            user = new
            {
                id = user.Id,
                username = user.Username,
                syncStrategy = user.SyncStrategy
            }
        });
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

        return Ok(new
        {
            id = user.Id,
            username = user.Username,
            syncStrategy = user.SyncStrategy
        });
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

        if (request.Strategy != "trakt" && request.Strategy != "database")
        {
            return BadRequest(new { error = "INVALID_STRATEGY", message = "Strategy must be 'trakt' or 'database'" });
        }

        await _userRepository.UpdateSyncStrategyAsync(userId, request.Strategy);
        return Ok(new { success = true, strategy = request.Strategy });
    }
}

public record RegisterRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record UpdateSyncStrategyRequest(string Strategy);
