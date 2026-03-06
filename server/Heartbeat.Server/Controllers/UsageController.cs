using Heartbeat.Core.DTOs;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

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

            var deviceId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await _service.SaveUsageAsync(deviceId, request);
            return Ok();
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<AppUsageResponse>), 200)]
        public async Task<IActionResult> GetUsage(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var result = await _service.GetUsageAsync(deviceId, start, end);
            return Ok(result);
        }
    }
}
