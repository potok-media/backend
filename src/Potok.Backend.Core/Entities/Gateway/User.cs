using System;

namespace Potok.Backend.Core.Entities.Gateway;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string SyncStrategy { get; set; } = "none";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long? TelegramId { get; set; }
    public string? TelegramUsername { get; set; }
}
