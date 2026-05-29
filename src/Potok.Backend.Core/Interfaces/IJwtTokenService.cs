using System;
using System.Security.Claims;

namespace Potok.Backend.Core.Interfaces;

public interface IJwtTokenService
{
    string GenerateToken(Guid userId, string username);
    ClaimsPrincipal? ValidateToken(string token);
}
