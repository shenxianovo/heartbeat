using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Reports;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    // 查询端点统一用 ActionResult<T>:响应 schema 由返回类型自动推断进 OpenAPI,
    // NSwag 客户端才能生成带类型的方法(IActionResult 是不透明盒子,会生成 Promise<void>)。
    //
    // 可见性门（ADR-025）：private 用户对匿名/他人一律 404（不泄露用户名存在性），
    // 本人带 JWT（sub == User.Id）不受 IsPublic 影响。无 [Authorize]，但携带
    // Bearer 时认证中间件仍会填充 principal，据此识别本人。
    [ApiController]
    [Route("api/v1/users/{username}")]
    public class PublicUserController(
        UserService userService,
        DeviceService deviceService,
        ReportService reportService,
        UsageService usageService,
        AppService appService,
        InputEventService inputEventService,
        RecapService recapService,
        ICurrentUserService currentUser) : ControllerBase
    {
        /// <summary>解析用户名并施加可见性门：不存在或对当前调用者不可见都返回 null（上层 404）。</summary>
        private async Task<User?> ResolveVisibleAsync(string username)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return null;

            if (user.IsPublic) return user;
            return currentUser.GetUserIdOrNull() == user.Id ? user : null;
        }

        [HttpGet("devices")]
        [EndpointName("getUserDevices")]
        public async Task<ActionResult<List<DeviceInfoResponse>>> GetDevices(string username)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            return await deviceService.GetAllAsync(user.Id);
        }

        [HttpGet("reports/daily")]
        [EndpointName("getUserDailyReport")]
        [ProducesResponseType<DailyReportResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<CalendarWindowError>(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<DailyReportResponse>> GetDailyReport(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] LocalCalendarWindowEnvelope window)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            var result = await reportService.GetDailyReportAsync(user.Id, deviceId, window);
            return result.Error == null ? result.Report! : BadRequest(result.Error);
        }

        [HttpGet("reports/weekly")]
        [EndpointName("getUserWeeklyReport")]
        [ProducesResponseType<WeeklyReportResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<CalendarWindowError>(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<WeeklyReportResponse>> GetWeeklyReport(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] LocalCalendarWindowEnvelope window)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            var result = await reportService.GetWeeklyReportAsync(user.Id, deviceId, window);
            return result.Error == null ? result.Report! : BadRequest(result.Error);
        }


        [HttpGet("recaps/daily")]
        [EndpointName("getUserDailyRecap")]
        [ProducesResponseType<DailyRecapResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<CalendarWindowError>(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<DailyRecapResponse>> GetDailyRecap(
            string username,
            [FromQuery] LocalCalendarWindowEnvelope window,
            CancellationToken ct)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            var validation = LocalCalendarWindowValidator.ResolveDay(window);
            if (validation.Error != null) return BadRequest(validation.Error);

            // 公开路径只读同一个验证后 WindowKey 的缓存：访客不能触发生成。
            var recap = await recapService.GetCachedDailyRecapAsync(user.Id, validation.Window!, ct);
            return recap == null ? NotFound() : recap;
        }

        [HttpGet("usage")]
        [EndpointName("getUserUsage")]
        public async Task<ActionResult<List<AppUsageResponse>>> GetUsage(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            return await usageService.GetUsageAsync(user.Id, deviceId, start, end);
        }

        [HttpGet("segments")]
        [EndpointName("getUserSegments")]
        public async Task<ActionResult<List<SegmentResponse>>> GetSegments(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] string? source,
            [FromQuery] long? appId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            return await usageService.GetSegmentsAsync(user.Id, deviceId, source, appId, start, end);
        }

        [HttpGet("apps")]
        [EndpointName("getUserApps")]
        public async Task<ActionResult<List<AppInfoResponse>>> GetApps(string username)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            return await appService.GetAppsForUserAsync(user.Id);
        }

        // 图标端点匿名可达但受同一可见性门约束；private owner 的 Dashboard 通过
        // authHttp 拉取 Blob 以携带 JWT。owner 上下文仍取自 URL 的 username
        //（图标按 owner 隔离，ADR-025）。
        [HttpGet("apps/{appId:long}/icon")]
        [EndpointName("getUserAppIcon")]
        public async Task<IActionResult> GetAppIcon(string username, long appId)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            var iconData = await appService.GetIconAsync(user.Id, appId);
            if (iconData == null) return NotFound();

            return File(iconData, "image/png");
        }

        [HttpGet("devices/{deviceId:long}/status")]
        [EndpointName("getUserDeviceStatus")]
        public async Task<ActionResult<DeviceStatusResponse>> GetDeviceStatus(string username, long deviceId)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            var status = await deviceService.GetStatusAsync(deviceId, user.Id);
            if (status == null) return NotFound();
            return status;
        }

        [HttpGet("input-events/counts")]
        [EndpointName("getUserInputCounts")]
        public async Task<ActionResult<InputCountsResponse>> GetInputCounts(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            return await inputEventService.GetCountsAsync(user.Id, deviceId, start, end);
        }

        [HttpGet("input-events/key-frequency")]
        [EndpointName("getUserKeyFrequency")]
        public async Task<ActionResult<KeyFrequencyResponse>> GetKeyFrequency(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await ResolveVisibleAsync(username);
            if (user == null) return NotFound();

            return await inputEventService.GetKeyFrequencyAsync(user.Id, deviceId, start, end);
        }
    }
}
