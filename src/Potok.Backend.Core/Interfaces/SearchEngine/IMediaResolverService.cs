namespace Potok.Backend.Core.Interfaces.SearchEngine;

public interface IMediaResolverService
{
    Task<(string? search, string? altname)> ResolveKpImdb(string? search, string? altname);
}