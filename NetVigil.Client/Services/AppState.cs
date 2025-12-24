namespace NetVigil.Client.Services
{
    public class AppState
    {
        // Пока можно оставить пустым или хранить тут глобальные настройки
        public event Action? OnChange;
        private void NotifyStateChanged() => OnChange?.Invoke();
    }
}