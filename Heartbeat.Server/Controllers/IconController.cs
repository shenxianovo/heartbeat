using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using server.Data;
using server.Entities;

namespace server.Controllers
{
    [ApiController]
    [Route("api/v1/icons")]
    public class IconController(AppDbContext db) : ControllerBase
    {
        private readonly AppDbContext _db = db;

        /// <summary>
        /// 获取应用图标
        /// </summary>
        [HttpGet("{appName}")]
        public async Task<IActionResult> Get(string appName)
        {
            var icon = await _db.AppIcons
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.AppName == appName);

            if (icon == null)
                return NotFound();

            return File(icon.IconData, "image/png");
        }

        /// <summary>
        /// 上传应用图标（幂等，已有则覆盖）
        /// </summary>
        [HttpPost("{appName}")]
        public async Task<IActionResult> Upload(string appName, IFormFile icon, [FromForm] string? apiKey)
        {
            if (icon == null || icon.Length == 0)
                return BadRequest("Icon file is required.");

            if (icon.Length > 1024 * 1024) // 1MB 限制
                return BadRequest("Icon file too large (max 1MB).");

            using var ms = new MemoryStream();
            await icon.CopyToAsync(ms);
            var data = ms.ToArray();

            var existing = await _db.AppIcons
                .FirstOrDefaultAsync(x => x.AppName == appName);

            if (existing != null)
            {
                existing.IconData = data;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _db.AppIcons.Add(new AppIcon
                {
                    AppName = appName,
                    IconData = data,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            return Ok();
        }

        /// <summary>
        /// 获取所有已有图标的应用名列表
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var names = await _db.AppIcons
                .AsNoTracking()
                .Select(x => x.AppName)
                .ToListAsync();

            return Ok(names);
        }
    }
}
