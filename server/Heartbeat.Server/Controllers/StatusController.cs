using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Heartbeat.Server.Data;
using Heartbeat.Core.DTOs;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 客户端上传设备状态
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/v1/devices/heartbeat")]
    public class StatusController(AppDbContext db) : ControllerBase
    {
        private readonly AppDbContext _db = db;

        [HttpPost]
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
