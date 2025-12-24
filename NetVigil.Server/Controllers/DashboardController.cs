using Microsoft.AspNetCore.Mvc;
using NetVigil.Server.Services;
using NetVigil.Shared;

namespace NetVigil.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DashboardController : ControllerBase
    {
        private readonly SimulationService _storage; // Бывший SimulationService

        public DashboardController(SimulationService storage)
        {
            _storage = storage;
        }

        [HttpGet("stats")]
        public ActionResult<SystemStats> GetStats()
        {
            return Ok(_storage.GetStats());
        }

        [HttpGet("devices")]
        public ActionResult<List<NetworkDevice>> GetDevices()
        {
            return Ok(_storage.GetAllDevices());
        }
        [HttpGet("test-telegram")]
        public async Task<IActionResult> TestTelegram([FromServices] NetVigil.Server.Services.NotificationService notifier)
        {
            await notifier.SendNewDeviceAlert("TEST-DEVICE", "0.0.0.0");
            return Ok("Попытка отправки выполнена. Проверь чат.");
        }
    }
}