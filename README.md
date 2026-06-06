# NetVigil

Веб-приложение для мониторинга локальной сети: автоматический discovery устройств, real-time учёт трафика, ML-детекция аномалий, оператор-флаги, Telegram-уведомления.

## Стек

- **.NET 8** — Server (ASP.NET Core), Agent (Generic Host), Client (Blazor WebAssembly)
- **gRPC** — канал Agent → Server (HTTP/2 plaintext)
- **EF Core** — SQLite (dev) / TimescaleDB-Postgres (prod)
- **Isolation Forest** (custom impl) + Rolling Z-Score — детекторы аномалий
- **Telegram.Bot** — нотификации
- **SharpPcap / Npcap / libpcap** — опциональный per-MAC packet capture
- **JWT + PBKDF2-SHA256** — аутентификация
- **nginx** — reverse-proxy для клиента в docker

## Режимы агента

`appsettings.json` → `Agent:Mode`:

- **`ArpScan`** (default) — discovery через ICMP-ping /24 + ARP-кэш + NBNS/mDNS, метрики только для self-host;
- **`GatewaySniffer`** — то же + per-MAC traffic accounting через packet capture. Требует **Npcap** (Windows) или libpcap + `cap_net_raw` (Linux). Имеет смысл только когда хост — гейтвей сети (Windows ICS, Linux hotspot, OpenWrt).

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

## NetVigil.LoadTest (вспомогательный)

`NetVigil.LoadTest/` **не является частью продукта** — это локальный синтетический
gRPC-флудер для стресса `MetricsService.IngestSample` и фронтэнд-латенции в
ходе перформанс-итераций.
