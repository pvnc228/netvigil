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

        [HttpGet("stats")]
        public ActionResult<SystemStats> GetStats()
        {
            _sim.Tick(); // Каждый запрос чуть-чуть меняет цифры
            return Ok(_sim.Stats);
        }

        [HttpGet("devices")]
        public ActionResult<List<NetworkDevice>> GetDevices()
        {
            return Ok(_sim.Devices);
        }

        // --- ADMIN COMMANDS ---

        [HttpPost("spawn")]
        public IActionResult SpawnDevice()
        {
            _sim.AddRandomDevice();
            return Ok();
        }

        [HttpPost("attack")]
        public IActionResult TriggerAttack()
        {
            _sim.TriggerAttack();
            return Ok();
        }

        [HttpPost("reset")]
        public IActionResult Reset()
        {
            _sim.Reset();
            return Ok();
        }
    }
}