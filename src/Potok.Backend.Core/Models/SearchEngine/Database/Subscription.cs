namespace Potok.Backend.Core.Models.SearchEngine.Database;

public class Subscription
{
    public Guid Id { get; set; } // PK
    public long TmdbId { get; set; } // FK
    public string Media { get; set; } = string.Empty;
    public string Uid { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}