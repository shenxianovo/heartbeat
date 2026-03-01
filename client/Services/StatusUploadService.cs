using client.DTOs;
using Serilog;
using System.Net.Http.Json;

namespace client.Services
{
    public class StatusUploadService
    {
        private readonly string _statusUrl;
        private readonly string _apiKey;
        private readonly HttpClient _client = new();

        public StatusUploadService(string apiUrl, string apiKey, string deviceName)
        {
            // apiUrl: .../api/v1/usage -> .../api/v1/devices/{deviceName}/status
            var baseUrl = apiUrl.TrimEnd('/').EndsWith("/usage", StringComparison.OrdinalIgnoreCase)
                ? apiUrl[..apiUrl.LastIndexOf("/usage", StringComparison.OrdinalIgnoreCase)]
                : apiUrl.TrimEnd('/');
            _statusUrl = $"{baseUrl}/devices/{Uri.EscapeDataString(deviceName)}/status";
            _apiKey = apiKey;
            Log.Information("状态上传地址: {Url}", _statusUrl);
        }

        public async Task UploadAsync(string? currentApp)
        {
            var dto = new DeviceStatusRequest
            {
                ApiKey = _apiKey,
                CurrentApp = currentApp ?? string.Empty
            };

            try
            {
                var res = await _client.PostAsJsonAsync(_statusUrl, dto);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Log.Warning("状态上传失败 [{StatusCode}]: {Body}", (int)res.StatusCode, body);
                    return;
                }
                Log.Debug("状态上传成功: {App}", currentApp ?? "(无)");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "状态上传失败（网络异常）");
            }
        }
    }
}
