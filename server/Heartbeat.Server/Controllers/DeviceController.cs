using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Heartbeat.Server.Data;
using Heartbeat.Core.DTOs;

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
    }
}
