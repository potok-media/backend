using System;

namespace Potok.Backend.Core.Entities.Gateway;

public class UserTraktToken
{
    public Guid UserId { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
