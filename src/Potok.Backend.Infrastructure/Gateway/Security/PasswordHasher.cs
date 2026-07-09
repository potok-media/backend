using Microsoft.AspNetCore.Identity;
using Potok.Backend.Core.Interfaces.Gateway;

namespace Potok.Backend.Infrastructure.Gateway.Security;

public class PasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<string> _hasher = new();

    public string HashPassword(string password)
    {
        return _hasher.HashPassword("default", password);
    }

    public bool VerifyPassword(string password, string hashedPassword)
    {
        var result = _hasher.VerifyHashedPassword("default", hashedPassword, password);
        return result == PasswordVerificationResult.Success;
    }
}
