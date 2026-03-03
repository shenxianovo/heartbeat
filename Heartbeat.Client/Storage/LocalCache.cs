using client.DTOs;
using System.Text.Json;

namespace client.Storage
{
    public class LocalCache
    {
        private readonly string _filePath;
        private readonly ReaderWriterLockSlim _lock = new();
        private List<AppUsageItem> _cache;

        public LocalCache(string filePath)
        {
            _filePath = filePath;
            _cache = LoadInternal();
        }

        public void Add(List<AppUsageItem> items)
        {
            if (items == null || items.Count == 0) return;

            _lock.EnterWriteLock();
            try
            {
                _cache.AddRange(items);
                SaveInternal();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public List<AppUsageItem> Load()
        {
            _lock.EnterReadLock();
            try
            {
                return new List<AppUsageItem>(_cache);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _cache.Clear();
                SaveInternal();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void SaveInternal()
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch { }
        }

        private List<AppUsageItem> LoadInternal()
        {
            if (!File.Exists(_filePath)) return new List<AppUsageItem>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<AppUsageItem>>(json) ?? new List<AppUsageItem>();
            }
            catch
            {
                return new List<AppUsageItem>();
            }
        }
    }
}
