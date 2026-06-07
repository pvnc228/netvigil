# NetVigil

Веб-приложение для мониторинга локальной сети.

## Стек

- **.NET 8** - Server, Agent, Client
- **gRPC** - канал Agent → Server
- **EF Core** - TimescaleDB-Postgres 
- **Isolation Forest** - детектор аномалий
- **Telegram.Bot** - уведомления
- **SharpPcap / Npcap / libpcap** - опциональный per-MAC packet capture
- **JWT + PBKDF2-SHA256** - аутентификация
- **nginx** - reverse-proxy в docker

## Режимы агента

`appsettings.json` → `Agent:Mode`:

- **`ArpScan`** (default) - discovery через ICMP-ping /24 + ARP-кэш + NBNS/mDNS, метрики только для self-host;
- **`GatewaySniffer`** — то же + per-MAC traffic accounting через packet capture. Требует **Npcap** (Windows) или libpcap + `cap_net_raw` (Linux). Имеет смысл только когда хост - гейтвей сети (Windows ICS, Linux hotspot, OpenWrt).

## Конфигурация

Основное в `NetVigil.Server/appsettings.json`:

| Ключ | Назначение |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQLite-путь или Postgres-строка (с `Host=`) |
| `Jwt:Secret` | HMAC-ключ. Пустой → генерится при каждом старте (= инвалидация токенов) |
| `Jwt:ExpiryMinutes` | Default 480 (8ч) |
| `Auth:DefaultAdmin:{Username,Password}` | Seed при пустых Users |
| `Anomaly:Detector` | `isolation-forest` или `zscore` |
| `Anomaly:ModelPath` | JSON-снэпшот леса (default `/app/data/anomaly-model.json`) |
| `DataProtection:KeyPath` | Keyring для шифрования Telegram-токена |
| `Cors:AllowedOrigins[]` | Пустой → fallback на dev-origins |

## Развертывание
- docker-compose up --build -d - поднятие основных модулей системы
- dotnet run --project NetVigil.Agent - запуск агента сканирования подсети

## NetVigil.LoadTest (вспомогательный)

`NetVigil.LoadTest/` **не является частью продукта** — это локальный синтетический
gRPC-флудер для стресса `MetricsService.IngestSample` и фронтэнд-латенции в
ходе перформанс-итераций.
