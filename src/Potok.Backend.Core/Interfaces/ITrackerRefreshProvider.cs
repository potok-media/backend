namespace Potok.Backend.Core.Interfaces;

/// <summary>
///     Tracker refresh stub. Defaults to SearchAsync.
/// </summary>
public interface ITrackerRefreshProvider : ITrackerSearch
{
    Task InvokeAsync();
}