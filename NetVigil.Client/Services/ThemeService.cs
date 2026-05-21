using Blazored.LocalStorage;
using Microsoft.JSInterop;

namespace NetVigil.Client.Services
{
    public enum AppTheme { Dark, Light }

    public class ThemeService
    {
        private const string Key = "netvigil.theme";
        private readonly ILocalStorageService _storage;
        private readonly IJSRuntime _js;
        public AppTheme Current { get; private set; } = AppTheme.Dark;

        public event Action? Changed;

        public ThemeService(ILocalStorageService storage, IJSRuntime js)
        {
            _storage = storage;
            _js = js;
        }

        public async Task InitAsync()
        {
            try
            {
                var stored = await _storage.GetItemAsStringAsync(Key);
                if (!string.IsNullOrEmpty(stored) && Enum.TryParse<AppTheme>(stored.Trim('"'), true, out var t))
                {
                    Current = t;
                }
            }
            catch { }
            await ApplyAsync();
        }

        public async Task SetAsync(AppTheme theme)
        {
            Current = theme;
            try { await _storage.SetItemAsStringAsync(Key, theme.ToString()); } catch { }
            await ApplyAsync();
            Changed?.Invoke();
        }

        public Task ToggleAsync() => SetAsync(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

        private async Task ApplyAsync()
        {
            try
            {
                var script = Current == AppTheme.Light
                    ? "document.documentElement.classList.add('light-mode'); document.body.classList.add('light-mode');"
                    : "document.documentElement.classList.remove('light-mode'); document.body.classList.remove('light-mode');";
                await _js.InvokeVoidAsync("eval", script);
            }
            catch { }
        }
    }
}
