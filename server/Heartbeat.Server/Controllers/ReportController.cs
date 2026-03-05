using Microsoft.AspNetCore.Mvc;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/reports")]
    public class ReportController(ReportService reportService) : ControllerBase
    {
        private readonly ReportService _reportService = reportService;

        [HttpGet("daily")]
        public async Task<IActionResult> GetDailyReport(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var targetDate = date ?? DateTimeOffset.UtcNow;
            var report = await _reportService.GetDailyReportAsync(deviceId, targetDate);
            return Ok(report);
        }

        [HttpGet("weekly")]
        public async Task<IActionResult> GetWeeklyReport(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var targetDate = date ?? DateTimeOffset.UtcNow;
            var report = await _reportService.GetWeeklyReportAsync(deviceId, targetDate);
            return Ok(report);
        }
    }
}
