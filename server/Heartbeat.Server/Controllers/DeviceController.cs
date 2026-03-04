using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Heartbeat.Server.Data;
using Heartbeat.Core.DTOs;

namespace Heartbeat.Server.Controllers
{
    [Route("api/v1/devices")]
    public class DeviceController(AppDbContext db) : ControllerBase
    {
        private readonly AppDbContext _db = db;

        [HttpGet]
        public async Task<List<string>> GetDevices()
        {
            return await _db.Devices.Select(x => x.DeviceName).ToListAsync();
        }

        [HttpGet("{deviceName}/status")]
        public async Task<IActionResult> GetStatus([FromRoute] string deviceName)
        {
            var device = await _db.Devices
                .Where(d => d.DeviceName == deviceName)
                .Select(d => new DeviceStatusResponse
                {
                    CurrentApp = d.CurrentApp,
                    LastSeen = d.LastSeen
                })
                .FirstOrDefaultAsync();

            if (device == null) return NotFound();

            return Ok(device);
        }

        [Authorize]
        [HttpPost("status")]
        public async Task<IActionResult> UpdateStatus(
            [FromBody] DeviceStatusRequest status)
        {
            var deviceName = User.Identity!.Name!;
            var device = await _db.Devices.FirstOrDefaultAsync(x => x.DeviceName == deviceName);

            if (device == null) return NotFound();
            device.CurrentApp = status.CurrentApp;
            device.LastSeen = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}
