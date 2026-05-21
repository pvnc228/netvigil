using System;
using System.Collections.Generic;

namespace NetVigil.Shared
{
    public class AgentInfoSnapshot
    {
        public string AgentId { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public string InterfaceName { get; set; } = string.Empty;
        public string InterfaceDesc { get; set; } = string.Empty;
        public string LocalIp { get; set; } = string.Empty;
        public string SubnetCidr { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string GrpcTarget { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime LastSeen { get; set; }
    }
}
