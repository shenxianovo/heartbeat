using client.Models;
using client.Utils;
using Serilog;

namespace client.Services
{
    public class IconUploadService(Config config, HttpClient httpClient)
    {
        private readonly string _iconsUrl = $"{config.ApiBaseUrl}/icons";

        // 已上传过的应用名集合（内存缓存，避免重复上传）
        private readonly HashSet<string> _uploadedApps = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 检查并上传应用图标（幂等，已上传过的不会重复上传）
        /// </summary>
        public async Task EnsureIconUploadedAsync(string appName)
        {
            if (_uploadedApps.Contains(appName))
                return;

            Log.Debug("检查图标: {App}", appName);

            try
            {
                // 先检查服务端是否已有该图标
                var checkRes = await httpClient.GetAsync($"{_iconsUrl}/{Uri.EscapeDataString(appName)}");
                if (checkRes.IsSuccessStatusCode)
                {
                    Log.Debug("服务端已有图标，跳过: {App}", appName);
                    _uploadedApps.Add(appName);
                    return;
                }

                Log.Debug("服务端无图标，开始提取: {App}", appName);

                // 提取图标
                var iconData = IconHelper.GetIconPngByProcessName(appName);
                if (iconData == null || iconData.Length == 0)
                {
                    Log.Warning("无法提取图标，跳过上传: {App}", appName);
                    return;
                }

                // 上传
                Log.Debug("正在上传图标: {App}，大小 {Size} bytes", appName, iconData.Length);
                using var content = new MultipartFormDataContent();
                content.Add(new ByteArrayContent(iconData), "icon", $"{appName}.png");
                content.Add(new StringContent(config.ApiKey), "apiKey");

                var res = await httpClient.PostAsync($"{_iconsUrl}/{Uri.EscapeDataString(appName)}", content);
                if (res.IsSuccessStatusCode)
                {
                    _uploadedApps.Add(appName);
                    Log.Information("图标上传成功: {App}", appName);
                }
                else
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Log.Warning("图标上传失败 [{StatusCode}]: {App}，响应: {Body}", (int)res.StatusCode, appName, body);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "图标上传异常: {App}", appName);
            }
        }
    }
}
