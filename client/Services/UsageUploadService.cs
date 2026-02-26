using client.DTOs;
using client.Storage;
using System.Net.Http.Json;

namespace client.Services
{
    public class UsageUploadService
    {
        private readonly string _apiUrl;
        private readonly string _apiKey;
        private readonly string _deviceName;
        private readonly LocalCache _cache;
        private readonly HttpClient _client = new();

        public UsageUploadService(string apiUrl, string apiKey, string deviceName, LocalCache cache)
        {
            _apiUrl = apiUrl;
            _apiKey = apiKey;
            _deviceName = deviceName;
            _cache = cache;
        }

        private UsageUploadRequest MapToDto(List<AppUsageItem> items)
        {
            return new UsageUploadRequest
            {
                DeviceName = _deviceName,
                ApiKey = _apiKey,
                Usages = items.ConvertAll(i => new AppUsageItem
                {
                    AppName = i.AppName,
                    StartTime = i.StartTime,
                    EndTime = i.EndTime
                })
            };
        }

        public async Task UploadAsync(List<AppUsageItem> usages)
        {
            var dto = MapToDto(usages);
            try
            {
                var res = await _client.PostAsJsonAsync(_apiUrl, dto);
                res.EnsureSuccessStatusCode();
                Console.WriteLine($"Uploaded {usages.Count} items.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Upload failed, saving to cache: {ex.Message}");
                _cache.Add(usages);
            }
        }

        public async Task UploadCachedAsync()
        {
            var cached = _cache.Load();
            if (cached.Count == 0) return;

            await UploadAsync(cached);
            _cache.Clear();
        }
    }
}
