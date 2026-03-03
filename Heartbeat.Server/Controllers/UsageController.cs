using Microsoft.AspNetCore.Mvc;
using server.DTOs;
using server.Services;

namespace server.Controllers
{
    [ApiController]
    [Route("api/v1/usage")]
    public class UsageController(UsageService service) : ControllerBase
    {
        private readonly UsageService _service = service;

        [HttpPost]
        public async Task<IActionResult> Upload([FromBody] UsageUploadRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.DeviceName))
                return BadRequest("DeviceName is required.");

            if (string.IsNullOrWhiteSpace(request.ApiKey))
                return BadRequest("ApiKey is required.");

            if (request.Usages == null || request.Usages.Count == 0)
                return BadRequest("Usages cannot be empty.");

            if (!await _service.ValidateApiKeyAsync(request.DeviceName, request.ApiKey))
                return Unauthorized("Invalid ApiKey for this device.");

            await _service.SaveUsageAsync(request);
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
