using Potok.Backend.Core.Interfaces.Gateway;
using Potok.Backend.Core.Models.SearchEngine.Database;
using Potok.Backend.Core.Utils;

namespace Potok.Backend.Infrastructure.SearchEngine.Services;

public class SubscribeService : ISubscribeService
{
    private readonly IMediaResolverService _mediaResolver;
    private readonly ISubscriptionRepository _repository;
    private readonly IQueriesRepository _queriesRepository;

    public SubscribeService(
        IMediaResolverService mediaResolver,
        ISubscriptionRepository repository,
        IQueriesRepository queriesRepository)
    {
        _mediaResolver = mediaResolver;
        _repository = repository;
        _queriesRepository = queriesRepository;
    }

    public async Task<bool> SubscribeAsync(long tmdbId, string media, string uid)
    {
        var (search, altname) = await _mediaResolver.ResolveKpImdb(tmdbId.ToString(), null);
        var trackerQuery = StringConvert.ClearTitle($"{search} {altname}".Trim());

        if (string.IsNullOrWhiteSpace(trackerQuery))
            return false;
        
        await _queriesRepository.TrackSearchQueryAsync(tmdbId, trackerQuery);

        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            Uid = uid,
            TmdbId = tmdbId,
            Media = media ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _repository.AddAsync(subscription);
        return true;
    }

    public async Task<bool> UnSubscribeAsync(long tmdbId, string media, string uid)
    {
        await _repository.RemoveAsync(tmdbId, uid, media);
        await _queriesRepository.RemoveQueryIfNoSubscriptionsAsync(tmdbId);
        return true;
    }

    public async Task<bool> CheckSubscribeAsync(long tmdbId, string media, string uid)
    {
        return await _repository.ExistsAsync(tmdbId, uid, media);
    }

    public async Task<IReadOnlyCollection<UserSubscriptionItem>> GetUserSubscriptionsAsync(string uid)
    {
        return await _queriesRepository.GetUserSubscriptionsAsync(uid);
    }
}