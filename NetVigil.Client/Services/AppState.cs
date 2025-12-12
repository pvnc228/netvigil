namespace NetVigil.Client.Services
{
    public class AppState
    {
        public bool IsDemoMode { get; private set; } = false;
        public bool IsRealMode { get; private set; } = false;
        public bool IsModeSelected => IsDemoMode || IsRealMode;

        // Событие, чтобы интерфейс знал, что что-то изменилось
        public event Action? OnChange;

        public void SetDemoMode()
        {
            IsDemoMode = true;
            IsRealMode = false;
            NotifyStateChanged();
        }

        public void SetRealMode()
        {
            IsDemoMode = false;
            IsRealMode = true;
            NotifyStateChanged();
        }

        public void ResetMode()
        {
            IsDemoMode = false;
            IsRealMode = false;
            NotifyStateChanged();
        }

        private void NotifyStateChanged() => OnChange?.Invoke();
    }
}