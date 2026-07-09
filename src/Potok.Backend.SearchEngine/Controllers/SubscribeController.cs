using Microsoft.AspNetCore.Mvc;
using Potok.Backend.Core.Interfaces.Gateway;

namespace Potok.Backend.SearchEngine.Controllers;

[ApiController]
[Route("api/v1/subscriptions")]
public class SubscribeController : ControllerBase
{
    private readonly ISubscribeService _subscribeService;

    public SubscribeController(ISubscribeService subscribeService)
    {
        _subscribeService = subscribeService;
    }

    /// <summary>
    ///     Начать отслеживание сериала
    /// </summary>
    /// <param name="tmdb">идентификатор сериала</param>
    /// <param name="media">название медиа</param>
    /// <param name="uid">уникальный идентификатор пользователя</param>
    [HttpPost("[action]")]
    public async Task<IActionResult> Subscribe(long tmdb, string media, string uid)
    {
        return Ok(new
        {
            result = await _subscribeService.SubscribeAsync(tmdb, media, uid)
        });
    }

    /// <summary>
    ///     Прекратить отслеживание сериала
    /// </summary>
    /// <param name="tmdb">идентификатор сериала</param>
    /// <param name="media">название медиа</param>
    /// <param name="uid">уникальный идентификатор пользователя</param>
    [HttpPost("[action]")]
    public async Task<IActionResult> UnSubscribe(long tmdb, string media, string uid)
    {
        return Ok(new
        {
            result = await _subscribeService.UnSubscribeAsync(tmdb, media, uid)
        });
    }

    /// <summary>
    ///     Проверить наличие отслеживания сериала
    /// </summary>
    /// <param name="tmdb">идентификатор сериала</param>
    /// <param name="media">название медиа</param>
    /// <param name="uid">уникальный идентификатор пользователя</param>
    [HttpPost("check-subscribe")]
    public async Task<IActionResult> CheckSubscribe(long tmdb, string media, string uid)
    {
        return Ok(
            new
            {
                result = await _subscribeService.CheckSubscribeAsync(tmdb, media, uid)
            });
    }

    /// <summary>
    ///     Получить список отслеживаемых сериалов
    /// </summary>
    /// <param name="uid">уникальный идентификатор пользователя</param>
    [HttpGet("subscribes")]
    public async Task<IActionResult> GetSubscribes(string uid)
    {
        return Ok(await _subscribeService.GetUserSubscriptionsAsync(uid));
    }
}