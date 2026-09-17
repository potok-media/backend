namespace Potok.Backend.Core.Models.SearchEngine;

public class StaleQuery
{
    public string Query { get; set; } = null!;
    public long TmdbId { get; set; }
}