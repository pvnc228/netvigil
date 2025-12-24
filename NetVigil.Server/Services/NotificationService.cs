using Telegram.Bot;

namespace NetVigil.Server.Services
{
    public class NotificationService
    {
        private readonly ILogger<NotificationService> _logger;
        private readonly TelegramBotClient _botClient;
        private readonly long _chatId = 756842822; // ТВОЙ ID (узнай у бота @userinfobot)

        public NotificationService(ILogger<NotificationService> logger)
        {
            _logger = logger;
            // Вставь сюда токен от BotFather
            _botClient = new TelegramBotClient("8558422758:AAGapl7v85goUI-CLz6A3g3lsqT4kY8MxpI");
        }

        public async Task SendNewDeviceAlert(string hostname, string ip)
        {
            var message = $"🚨 <b>ВНИМАНИЕ! НОВОЕ УСТРОЙСТВО</b>\n" +
                          $"📡 Имя: {hostname}\n" +
                          $"🌐 IP: {ip}\n" +
                          $"🕒 Время: {DateTime.Now:HH:mm:ss}";

            // Отправка в реальный телеграм
            try
            {
                await _botClient.SendMessage(_chatId, message, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Ошибка Telegram: {ex.Message}");
            }
        }
    }
}