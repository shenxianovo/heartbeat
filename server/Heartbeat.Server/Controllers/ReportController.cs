using Heartbeat.Core.DTOs.Reports;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/reports")]
    public class ReportController(ReportService reportService) : ControllerBase
    {
        private readonly ReportService _reportService = reportService;

        [HttpGet("daily")]
        [ProducesResponseType(typeof(DailyReportResponse), 200)]
        public async Task<IActionResult> GetDailyReport(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var targetDate = date ?? DateTimeOffset.UtcNow;
            var report = await _reportService.GetDailyReportAsync(deviceId, targetDate);
            return Ok(report);
        }

        [HttpGet("weekly")]
        [ProducesResponseType(typeof(WeeklyReportResponse), 200)]
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
