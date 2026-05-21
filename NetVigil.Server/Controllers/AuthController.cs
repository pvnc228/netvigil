using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NetVigil.Server.Data;
using NetVigil.Server.Services.Auth;
using NetVigil.Shared;

namespace NetVigil.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _auth;
        private readonly NetVigilDbContext _db;

        public AuthController(AuthService auth, NetVigilDbContext db)
        {
            _auth = auth;
            _db = db;
        }

        public class ChangePasswordRequest
        {
            public string CurrentPassword { get; set; } = string.Empty;
            public string NewPassword { get; set; } = string.Empty;
        }

        [HttpPost("login")]
        [EnableRateLimiting("login")]
        public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest(new { error = "Username and password are required." });

            var resp = await _auth.LoginAsync(req.Username, req.Password);
            if (resp is null)
                return Unauthorized(new { error = "Invalid credentials." });

            return Ok(resp);
        }

        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var username = User.Identity?.Name ?? "";
            var mustChange = await _db.Users
                .Where(u => u.Username == username)
                .Select(u => (bool?)u.MustChangePassword)
                .FirstOrDefaultAsync() ?? false;
            return Ok(new
            {
                username,
                role = User.IsInRole("Admin") ? "Admin" : "Viewer",
                mustChangePassword = mustChange
            });
        }

        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
        {
            var username = User.Identity?.Name ?? "";
            if (string.IsNullOrWhiteSpace(username)) return Unauthorized();
            var (ok, err) = AuthService.ValidatePassword(req.NewPassword);
            if (!ok) return BadRequest(new { error = err });
            var changed = await _auth.ChangePasswordAsync(username, req.CurrentPassword, req.NewPassword);
            if (!changed) return BadRequest(new { error = "Current password is incorrect." });
            return Ok();
        }
    }
}
