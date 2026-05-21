using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NetVigil.Server.Services;
using NetVigil.Shared;

namespace NetVigil.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DashboardController : ControllerBase
    {
        private readonly MetricsService _metrics;
        private readonly AgentRegistry _agents;

        public DashboardController(MetricsService metrics, AgentRegistry agents)
        {
            _metrics = metrics;
            _agents = agents;
        }

        private static readonly TimeSpan AgentStale = TimeSpan.FromSeconds(60);

        [HttpGet("agent-info")]
        public ActionResult<List<NetVigil.Shared.AgentInfoSnapshot>> GetAgentInfo()
            => Ok(_agents.GetActive(AgentStale));

        private const int AbsoluteDeviceCap = 5000;
        private const int MaxAnomalyLimit   = 500;

        [HttpGet("stats")]
        public ActionResult<SystemStats> GetStats() => Ok(_metrics.GetStats());

        [HttpGet("detector-stats")]
        public ActionResult<DetectorStats> GetDetectorStats() => Ok(_metrics.GetDetectorStats());

        [HttpGet("devices")]
        public ActionResult<List<NetworkDevice>> GetDevices(
            [FromQuery] int limit = 0,
            [FromQuery] int offset = 0)
        {
            var all = _metrics.GetAllDevices();
            offset = Math.Max(0, offset);
            int take = limit <= 0 ? AbsoluteDeviceCap : Math.Min(limit, AbsoluteDeviceCap);
            return Ok(all.Skip(offset).Take(take).ToList());
        }

        [HttpGet("anomalies")]
        public async Task<ActionResult<List<AnomalyEvent>>> GetAnomalies(
            [FromQuery] int limit = 50,
            CancellationToken ct = default)
        {
            limit = Math.Clamp(limit, 1, MaxAnomalyLimit);
            return Ok(await _metrics.GetRecentAnomaliesAsync(limit, ct));
        }

        [HttpGet("traffic-history")]
        public async Task<ActionResult<List<TrafficPoint>>> GetTrafficHistory([FromQuery] int minutes = 60, CancellationToken ct = default)
            => Ok(await _metrics.GetTrafficHistoryAsync(minutes, ct));

        [HttpPost("devices/{mac}/flag")]
        public ActionResult FlagDevice(string mac, [FromBody] FlagDeviceRequest req)
        {
            var actor = User?.Identity?.Name ?? "anonymous";
            var ok = _metrics.SetFlag(mac, req.IsFlagged, actor, req.Reason);
            return ok ? Ok(new { mac, isFlagged = req.IsFlagged }) : NotFound();
        }

        [HttpGet("devices/{mac}/flag-audit")]
        public async Task<ActionResult<List<DeviceFlagAudit>>> GetDeviceFlagAudit(string mac, [FromQuery] int limit = 50, CancellationToken ct = default)
            => Ok(await _metrics.GetFlagAuditAsync(mac, limit, ct));

        [HttpGet("flag-audit")]
        public async Task<ActionResult<List<DeviceFlagAudit>>> GetAllFlagAudit([FromQuery] int limit = 100, CancellationToken ct = default)
            => Ok(await _metrics.GetFlagAuditAsync(null, limit, ct));
    }
}
