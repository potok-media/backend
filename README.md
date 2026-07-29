<p align="center"><img src=".github/assets/logo.svg" width="120" alt="potok"></p>

# potok-gateway

API-шлюз / BFF для potok. Центральный аккаунт-сервис: пользователи, JWT-аутентификация,
история / избранное / watchlist, профили, Telegram-логин, Trakt-интеграция, watch-together (SignalR).

Выделен из моно-репозитория `Potok.Backend`. Стриминговое ядро (SearchEngine + TorrentGo) живёт
отдельно в **potok-streaming**; связь между сервисами — по HTTP.

## Состав

| Проект | Назначение |
|---|---|
| `Potok.Backend.Gateway` | ASP.NET Core host (контроллеры, SignalR-хабы) |
| `Potok.Backend.Infrastructure` | репозитории (Dapper), миграции (FluentMigrator, схема `potok-gateway`), Telegram/Trakt, кэш |
| `Potok.Backend.Core` | сущности, интерфейсы, модели Gateway-домена |
| `Potok.Backend.PluginBundler` | Go-утилита сборки плагинов (собирается билд-таргетом Gateway в Debug) |
| `Potok.Backend.CompositionTests` | xUnit: валидация DI-графа |

## Требования
- .NET SDK 10
- Go 1.23+ (для PluginBundler; нужен только в Debug-сборке)
- PostgreSQL (схема `potok-gateway`)

## Сборка и тесты
```bash
dotnet build potok-gateway.slnx -c Release
dotnet test potok-gateway.slnx -c Release
```

## Запуск (Docker)
```bash
cp .env.example .env   # отредактируй секреты (JWT, БД, Telegram)
docker compose up --build
```
Gateway применит миграции своей схемы на старте и поднимется на `GATEWAY_PORT` (по умолчанию 5000).

## База данных
Владеет схемой `potok-gateway`. Может делить один PostgreSQL с potok-streaming (у каждого своя схема) —
для этого укажи `DB_HOST` на общий инстанс и убери сервис `db` в одном из репозиториев.
