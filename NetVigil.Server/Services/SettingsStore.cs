using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetVigil.Server.Data;
using NetVigil.Shared;

namespace NetVigil.Server.Services
{
    public class SettingsStore
    {
        private const string Key = "app";
        private const string EncPrefix = "enc:v1:";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IDataProtector _protector;
        private readonly ILogger<SettingsStore> _logger;
        private AppSettings _cache = new();

        public SettingsStore(
            IServiceScopeFactory scopeFactory,
            IDataProtectionProvider dpProvider,
            ILogger<SettingsStore> logger)
        {
            _scopeFactory = scopeFactory;
            _protector = dpProvider.CreateProtector("NetVigil.Settings.TelegramToken.v1");
            _logger = logger;
        }

        public AppSettings Current => _cache;

        public async Task LoadAsync(CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);
            if (entry is null)
            {
                _cache = new AppSettings();
                return;
            }
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(entry.Value) ?? new AppSettings();
                settings.TelegramBotToken = DecryptToken(settings.TelegramBotToken);
                _cache = settings;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load settings, starting with defaults.");
                _cache = new AppSettings();
            }
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            _cache = settings;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetVigilDbContext>();
            var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);

            var toPersist = new AppSettings
            {
                TelegramEnabled        = settings.TelegramEnabled,
                TelegramBotToken       = EncryptToken(settings.TelegramBotToken),
                TelegramChatId         = settings.TelegramChatId,
                ScanIntervalSeconds    = settings.ScanIntervalSeconds,
                AnomalyThresholdZScore = settings.AnomalyThresholdZScore
            };
            var json = JsonSerializer.Serialize(toPersist);

            if (entry is null) db.Settings.Add(new SettingEntry { Key = Key, Value = json });
            else                entry.Value = json;
            await db.SaveChangesAsync(ct);
        }

        private string EncryptToken(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            if (plain.StartsWith(EncPrefix, StringComparison.Ordinal)) return plain;
            return EncPrefix + _protector.Protect(plain);
        }

        private string DecryptToken(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return string.Empty;
            if (!stored.StartsWith(EncPrefix, StringComparison.Ordinal))
            {
                return stored;
            }
            try
            {
                return _protector.Unprotect(stored[EncPrefix.Length..]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram token decryption failed; clearing.");
                return string.Empty;
            }
        }
    }
}
