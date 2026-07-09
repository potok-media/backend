namespace Potok.Backend.Core.Interfaces.SearchEngine;

/// <summary>
///     Tracker refresh stub. Defaults to SearchAsync.
/// </summary>
public interface ITrackerRefreshProvider : ITrackerSearch
{
    Task InvokeAsync();
}