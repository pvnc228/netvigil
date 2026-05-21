using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NetVigil.Server.Services;
using NetVigil.Shared;

namespace NetVigil.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class SettingsController : ControllerBase
    {
        private readonly SettingsStore _store;
        private readonly NotificationService _notifier;

        public SettingsController(SettingsStore store, NotificationService notifier)
        {
            _store = store;
            _notifier = notifier;
        }

        [HttpGet]
        public ActionResult<AppSettings> Get()
        {
            var s = _store.Current;
            return Ok(new AppSettings
            {
                TelegramEnabled = s.TelegramEnabled,
                TelegramBotToken = MaskToken(s.TelegramBotToken),
                TelegramChatId = s.TelegramChatId,
                ScanIntervalSeconds = s.ScanIntervalSeconds,
                AnomalyThresholdZScore = s.AnomalyThresholdZScore
            });
        }

        [HttpPut]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Update([FromBody] AppSettings req, CancellationToken ct)
        {
            var current = _store.Current;
            var updated = new AppSettings
            {
                TelegramEnabled = req.TelegramEnabled,
                TelegramBotToken = string.IsNullOrEmpty(req.TelegramBotToken) || req.TelegramBotToken.Contains('•')
                    ? current.TelegramBotToken
                    : req.TelegramBotToken,
                TelegramChatId = req.TelegramChatId,
                ScanIntervalSeconds = Math.Clamp(req.ScanIntervalSeconds, 5, 600),
                AnomalyThresholdZScore = Math.Clamp(req.AnomalyThresholdZScore, 1.0, 10.0)
            };
            await _store.SaveAsync(updated, ct);
            return Ok();
        }

        [HttpPost("test-telegram")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> TestTelegram()
        {
            var (ok, err) = await _notifier.SendTestAsync();
            if (ok) return Ok(new { ok = true });
            return Ok(new { ok = false, error = err });
        }

        private static string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;
            if (token.Length <= 6) return new string('•', token.Length);
            return token[..3] + new string('•', token.Length - 6) + token[^3..];
        }
    }
}
