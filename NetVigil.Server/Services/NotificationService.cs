using NetVigil.Shared;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace NetVigil.Server.Services
{
    public class NotificationService
    {
        private readonly ILogger<NotificationService> _logger;
        private readonly SettingsStore _settings;

        public NotificationService(ILogger<NotificationService> logger, SettingsStore settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public Task SendNewDeviceAlertAsync(string hostname, string ip)
        {
            var message = $"🆕 <b>Новое устройство в сети</b>\n" +
                          $"📡 Имя: {Escape(hostname)}\n" +
                          $"🌐 IP: <code>{Escape(ip)}</code>\n" +
                          $"🕒 {DateTime.Now:HH:mm:ss}";
            return SendAsync(message);
        }

        public Task SendAnomalyAlertAsync(AnomalyEvent ev)
        {
            var icon = ev.Severity switch
            {
                RiskLevel.Critical  => "🚨",
                RiskLevel.Anomalous => "⚠️",
                _                   => "ℹ️"
            };

            var message =
                $"{icon} <b>Аномалия трафика</b>\n" +
                $"📡 Устройство: {Escape(ev.DeviceName)}\n" +
                $"🌐 MAC: <code>{Escape(ev.DeviceMac)}</code>\n" +
                $"📊 Трафик: <b>{ev.Mbps:F1} Mbps</b>\n" +
                $"📈 Score: {ev.Score:F2} ({ev.Severity})\n" +
                $"💬 {Escape(ev.Description)}\n" +
                $"🕒 {ev.Timestamp.ToLocalTime():HH:mm:ss}";

            return SendAsync(message);
        }

        private async Task SendAsync(string message)
        {
            var s = _settings.Current;
            if (!s.TelegramEnabled || string.IsNullOrWhiteSpace(s.TelegramBotToken) || s.TelegramChatId == 0)
                return;
            try
            {
                var client = new TelegramBotClient(s.TelegramBotToken);
                await client.SendMessage(s.TelegramChatId, message, parseMode: ParseMode.Html);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram send failed");
            }
        }

        public async Task<(bool Ok, string? Error)> SendTestAsync()
        {
            var s = _settings.Current;
            if (!s.TelegramEnabled)
                return (false, "Telegram is disabled in Settings — enable it and save first.");
            if (string.IsNullOrWhiteSpace(s.TelegramBotToken))
                return (false, "Bot token is empty — paste the token from @BotFather and save first.");
            if (s.TelegramChatId == 0)
                return (false, "Chat ID is empty — see the setup instructions to find your chat_id.");

            var message =
                "✅ <b>NetVigil — test message</b>\n" +
                $"🕒 {DateTime.Now:HH:mm:ss}\n" +
                "If you can read this, the bot is wired up correctly.";
            try
            {
                var client = new TelegramBotClient(s.TelegramBotToken);
                await client.SendMessage(s.TelegramChatId, message, parseMode: ParseMode.Html);
                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram test message failed");
                return (false, ex.GetBaseException().Message);
            }
        }

        private static string Escape(string s) => s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
