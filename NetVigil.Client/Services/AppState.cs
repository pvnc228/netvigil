using NetVigil.Shared;

namespace NetVigil.Client.Services
{
    public class AppState : IDisposable
    {
        private readonly DashboardHubClient _hub;

        public int CriticalCount { get; private set; }
        public int AnomaliesLast24h { get; private set; }
        public IReadOnlyList<AgentInfoSnapshot> Agents { get; private set; } = Array.Empty<AgentInfoSnapshot>();
        public event Action? OnChange;

        public AppState(DashboardHubClient hub)
        {
            _hub = hub;
            _hub.OnSnapshot += HandleSnapshot;

            if (_hub.Latest is not null) HandleSnapshot(_hub.Latest);

            _ = _hub.StartAsync();
        }

        private void HandleSnapshot(DashboardSnapshot snap)
        {
            bool changed = false;
            if (snap.Stats.CriticalDevices != CriticalCount ||
                snap.Stats.AnomaliesLast24h != AnomaliesLast24h)
            {
                CriticalCount = snap.Stats.CriticalDevices;
                AnomaliesLast24h = snap.Stats.AnomaliesLast24h;
                changed = true;
            }

            if (!AgentsEqual(snap.Agents, Agents))
            {
                Agents = snap.Agents;
                changed = true;
            }

            if (changed) OnChange?.Invoke();
        }

        private static bool AgentsEqual(IReadOnlyList<AgentInfoSnapshot> a, IReadOnlyList<AgentInfoSnapshot> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].AgentId != b[i].AgentId ||
                    a[i].SubnetCidr != b[i].SubnetCidr ||
                    a[i].Mode != b[i].Mode ||
                    a[i].InterfaceName != b[i].InterfaceName) return false;
            }
            return true;
        }

        public void Dispose() => _hub.OnSnapshot -= HandleSnapshot;
    }
}
