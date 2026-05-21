using System.Collections.Concurrent;
using NetVigil.Shared;

namespace NetVigil.Server.Services
{
    public class AgentRegistry
    {
        private readonly ConcurrentDictionary<string, AgentInfoSnapshot> _agents = new();

        public void Update(AgentInfoSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.AgentId)) return;
            snapshot.LastSeen = DateTime.UtcNow;
            _agents[snapshot.AgentId] = snapshot;
        }

        public List<AgentInfoSnapshot> GetActive(TimeSpan staleAfter)
        {
            var cutoff = DateTime.UtcNow - staleAfter;
            return _agents.Values
                .Where(a => a.LastSeen >= cutoff)
                .OrderBy(a => a.Hostname)
                .ToList();
        }

        public List<AgentInfoSnapshot> GetAll()
            => _agents.Values.OrderBy(a => a.Hostname).ToList();
    }
}
