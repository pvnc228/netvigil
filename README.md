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
- **`GatewaySniffer`** - то же + per-MAC traffic accounting через packet capture. Требует Npcap или libpcap. Имеет смысл только когда хост - Windows ICS, Linux hotspot, OpenWrt

## Конфигурация

Основное в `NetVigil.Server/appsettings.json`:

| Ключ | Назначение |
|---|---|
| `ConnectionStrings:DefaultConnection` | Postgres-строка |
| `Jwt:Secret` | HMAC-ключ |
| `Jwt:ExpiryMinutes` | 8ч |
| `Auth:DefaultAdmin:{Username,Password}` | Seed при пустых Users |
| `Anomaly:Detector` | `isolation-forest` или `zscore` |
| `Anomaly:ModelPath` | JSON-снэпшот леса |
| `DataProtection:KeyPath` | Keyring для шифрования Telegram-токена |

## Развертывание
- docker-compose up --build -d - поднятие основных модулей системы
- dotnet run --project NetVigil.Agent - запуск агента сканирования подсети

## NetVigil.LoadTest 

`NetVigil.LoadTest/` **не является частью продукта** - это локальный синтетический
gRPC-флудер
