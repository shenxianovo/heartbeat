using Heartbeat.Client.Models;
using Heartbeat.Core.DTOs;
using Serilog;
using System.Net.Http.Json;

namespace Heartbeat.Client.Services
{
    public class StatusUploadService(Config config, HttpClient httpClient)
    {
        private readonly string _statusUrl = $"{config.ApiBaseUrl}/devices/heartbeat";

        public async Task UploadAsync(string? currentApp)
        {
            var dto = new DeviceStatusRequest
            {
                CurrentApp = currentApp ?? string.Empty
            };

            try
            {
                var res = await httpClient.PostAsJsonAsync(_statusUrl, dto);
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
