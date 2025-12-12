using Microsoft.AspNetCore.Mvc;
using NetVigil.Server.Services;
using NetVigil.Shared;

namespace NetVigil.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DashboardController : ControllerBase
    {
        private readonly SimulationService _sim;

        public DashboardController(SimulationService sim)
        {
            _sim = sim;
        }

        // GET: api/dashboard/stats?mode=demo
        [HttpGet("stats")]
        public ActionResult<SystemStats> GetStats([FromQuery] string mode = "real")
        {
            if (mode == "demo")
            {
                _sim.TickDemo(); 
                return Ok(_sim.DemoStats);
            }

            var realStats = new SystemStats
            {
                OnlineDevices = _sim.RealDevices.Count,
                TotalDevices = _sim.RealDevices.Count
            };
            return Ok(realStats);
        }

        // GET: api/dashboard/devices?mode=demo
        [HttpGet("devices")]
        public ActionResult<List<NetworkDevice>> GetDevices([FromQuery] string mode = "real")
        {
            if (mode == "demo")
            {
                return Ok(_sim.DemoDevices);
            }
            return Ok(_sim.RealDevices);
        }


        [HttpPost("spawn")]
        public IActionResult Spawn() { _sim.AddFakeDevice(); return Ok(); }

        [HttpPost("attack")]
        public IActionResult Attack() { _sim.StartDemoAttack(); return Ok(); }

        [HttpPost("reset")]
        public IActionResult Reset() { _sim.StopDemoAttack(); return Ok(); }
    }
}