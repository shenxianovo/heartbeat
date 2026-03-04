using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Heartbeat.Server.Services;
using Heartbeat.Core.DTOs;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/usage")]
    public class UsageController(UsageService service) : ControllerBase
    {
        private readonly UsageService _service = service;

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> Upload([FromBody] UsageUploadRequest request)
        {
            if (request.Usages == null || request.Usages.Count == 0)
                return BadRequest("Usages cannot be empty.");

            var deviceName = User.Identity!.Name!;
            await _service.SaveUsageAsync(deviceName, request);
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string? deviceName, [FromQuery] DateTimeOffset? date)
        {
            var result = await _service.GetUsageAsync(deviceName, date);
            return Ok(result);
        }
    }
}
