using System;

namespace Potok.Backend.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string SyncStrategy { get; set; } = "database"; // "trakt" or "database"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
