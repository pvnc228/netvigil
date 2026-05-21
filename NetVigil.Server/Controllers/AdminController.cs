using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NetVigil.Server.Services.Auth;
using NetVigil.Shared;

namespace NetVigil.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly AuthService _auth;

        public AdminController(AuthService auth)
        {
            _auth = auth;
        }

        [HttpGet("users")]
        public async Task<ActionResult<List<UserSummary>>> ListUsers()
            => Ok(await _auth.ListUsersAsync());

        [HttpPost("users")]
        public async Task<ActionResult<CreateUserResponse>> CreateUser([FromBody] CreateUserRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Username))
                return BadRequest(new { error = "Username is required." });

            var resp = await _auth.CreateUserAsync(req.Username, req.Role);
            if (resp is null)
                return Conflict(new { error = "Username already exists." });
            return Ok(resp);
        }

        [HttpDelete("users/{id:long}")]
        public async Task<IActionResult> DeleteUser(long id)
        {
            var ok = await _auth.DeleteUserAsync(id);
            if (!ok) return BadRequest(new { error = "Cannot delete (user not found or this is the last admin)." });
            return Ok();
        }

        [HttpPatch("users/{id:long}/role")]
        public async Task<IActionResult> UpdateRole(long id, [FromBody] UpdateUserRoleRequest req)
        {
            var ok = await _auth.UpdateUserRoleAsync(id, req.Role);
            if (!ok) return BadRequest(new { error = "Cannot change role (user not found or this is the last admin)." });
            return Ok();
        }

        [HttpPost("users/{id:long}/reset-password")]
        public async Task<ActionResult<CreateUserResponse>> ResetPassword(long id)
        {
            var generated = await _auth.ResetPasswordAsync(id);
            if (generated is null) return NotFound(new { error = "User not found." });
            return Ok(new CreateUserResponse { GeneratedPassword = generated });
        }
    }
}
