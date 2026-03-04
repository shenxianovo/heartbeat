using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Heartbeat.Server.Data;

namespace Heartbeat.Server.Authentication
{
    public static class ApiKeyDefaults
    {
        public const string Scheme = "ApiKey";
    }

    /// <summary>
    /// 自定义认证处理器，解析 Authorization: ApiKey {key}。
    /// 根据 ApiKey 查找设备，将 DeviceName 写入 ClaimsPrincipal。
    /// </summary>
    public class ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServiceScopeFactory scopeFactory)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // 解析 Authorization 头
            var authHeader = Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith($"{ApiKeyDefaults.Scheme} ", StringComparison.OrdinalIgnoreCase))
                return AuthenticateResult.NoResult();

            var apiKey = authHeader[$"{ApiKeyDefaults.Scheme} ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                return AuthenticateResult.Fail("ApiKey is empty.");

            // 使用 scope 获取 DbContext
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var device = await db.Devices
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.ApiKey == apiKey);

            if (device == null)
                return AuthenticateResult.Fail("Invalid ApiKey.");

            // 构建 ClaimsPrincipal
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, device.DeviceName)
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
    }
}
