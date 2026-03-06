using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/devices")]
    public class DeviceController(AppDbContext db) : ControllerBase
    {
        private readonly AppDbContext _db = db;

        [HttpGet]
        public async Task<List<DeviceInfoResponse>> GetDevices()
        {
            return await _db.Devices
                .Select(x => new DeviceInfoResponse
                {
                    Id = x.Id,
                    Name = x.DeviceName
                })
                .ToListAsync();
        }

        [HttpGet("{deviceId:long}")]
        [ProducesResponseType(typeof(DeviceStatusResponse), 200)]
        public async Task<IActionResult> GetDevice([FromRoute] long deviceId)
        {
            var device = await _db.Devices
                .Where(d => d.Id == deviceId)
                .Select(d => new DeviceStatusResponse
                {
                    Id = d.Id,
                    CurrentApp = d.CurrentApp,
                    LastSeen = d.LastSeen
                })
                .FirstOrDefaultAsync();

            if (device == null) return NotFound();
            return Ok(device);
        }

        [Authorize]
        [HttpPost("heartbeat")]
        public async Task<IActionResult> Upload([FromBody] DeviceStatusRequest status)
        {
            var deviceId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var device = await _db.Devices.FindAsync(deviceId);

            if (device == null) return NotFound();

            device.CurrentApp = status.CurrentApp;
            device.LastSeen = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}
