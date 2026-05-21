using System;

namespace NetVigil.Shared
{
    public class DeviceFlagAudit
    {
        public long Id { get; set; }
        public string DeviceMac { get; set; } = string.Empty;
        public bool IsFlagged { get; set; }
        public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
        public string ChangedBy { get; set; } = string.Empty;
        public string? Reason { get; set; }
    }
}
