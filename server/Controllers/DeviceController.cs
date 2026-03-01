using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using server.Data;
using server.DTOs;

namespace server.Controllers
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

            [HttpGet("/api/v1/devices/{deviceName}/status")]
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

            [HttpPost("/api/v1/devices/{deviceName}/status")]
            public async Task<IActionResult> UpdateStatus(
                [FromRoute] string deviceName,
                [FromBody] DeviceStatusRequest status)
            {
                var device = await _db.Devices.FirstOrDefaultAsync(x => x.DeviceName == deviceName);

                if (device == null) return NotFound();
                device.CurrentApp = status.CurrentApp;
                device.LastSeen = DateTimeOffset.UtcNow;

                await _db.SaveChangesAsync();
                return NoContent();
            }
    }
}
