using Heartbeat.Core.DTOs.Input;
using Heartbeat.Collection.Hub.Storage;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Tests.Storage;

/// <summary>
/// Durable cache contract: the public cache seam uses real files. Legacy JSON is read through an
/// explicit persistence DTO and migration, never directly as the current domain item.
/// </summary>
public class JsonFileCacheTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"heartbeat-cache-tests-{Guid.NewGuid()}");

    public JsonFileCacheTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, recursive: true);
    }

    private string TempPath(string name = "cache.json") => Path.Combine(_tempDirectory, name);

    private static JsonCacheFileFormat<InputEventItem, CurrentEventDto> CurrentFormat() =>
        new(
            version: 1,
            item => new CurrentEventDto(item.Id, item.EventType, item.Code, item.Timestamp),
            item => new InputEventItem
            {
                Id = item.Id,
                EventType = item.EventType,
                Code = item.Code,
                Timestamp = item.Timestamp
            });

    private JsonFileCache<InputEventItem> NewCache(string? path = null, int maxItems = 100_000) =>
        new(path ?? TempPath(), maxItems, CurrentFormat(), [LegacyMigration()]);

    private static JsonCacheMigration<InputEventItem, LegacyEventDto> LegacyMigration() =>
        JsonCacheMigration<InputEventItem, LegacyEventDto>.FromUnversionedArray(
            targetVersion: 1,
            item => new InputEventItem
            {
                Id = item.EventId,
                EventType = item.Kind,
                Code = item.LegacyCode,
                Timestamp = item.OccurredAt
            });

    private static InputEventItem Item(short code = 65) => new()
    {
        Id = Guid.CreateVersion7(),
        EventType = InputEventType.KeyDown,
        Code = code,
        Timestamp = DateTimeOffset.Parse("2026-08-11T12:34:56+08:00")
    };

    [Fact]
    public void Add_WritesExplicitVersionedEnvelope_AndRestartsFromIt()
    {
        var path = TempPath();
        using (var cache = NewCache(path))
            cache.Add([Item(42), Item(43)]);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());

        using var restarted = NewCache(path);
        Assert.Equal([42, 43], restarted.Load().Select(item => item.Code));
        Assert.Equal(CacheFileState.Ready, restarted.Status.State);
    }

    [Fact]
    public void LegacyArray_MigratesThroughLegacyDto_PreservesBackup_AndRestarts()
    {
        var path = TempPath();
        var id = Guid.CreateVersion7();
        var legacyJson = JsonSerializer.Serialize(new[]
        {
            new LegacyEventDto(id, InputEventType.MouseScroll, 2,
                DateTimeOffset.Parse("2026-08-10T01:02:03Z"))
        });
        File.WriteAllText(path, legacyJson);

        using var cache = NewCache(path);

        var migrated = Assert.Single(cache.Load());
        Assert.Equal(id, migrated.Id);
        Assert.Equal((short)2, migrated.Code);
        Assert.Equal(CacheFileState.Migrated, cache.Status.State);
        Assert.NotNull(cache.Status.BackupPath);
        Assert.Equal(legacyJson, File.ReadAllText(cache.Status.BackupPath!));

        using var restarted = NewCache(path);
        Assert.Equal(id, Assert.Single(restarted.Load()).Id);
        Assert.Equal(CacheFileState.Ready, restarted.Status.State);
    }

    [Fact]
    public void FailedMigration_LeavesOriginalRecoverable_AndBlocksNormalUse()
    {
        var path = TempPath();
        const string invalidLegacy = "[{\"eventId\":\"not-a-guid\",\"kind\":1,\"legacyCode\":65,\"occurredAt\":\"2026-08-11T00:00:00Z\"}]";
        File.WriteAllText(path, invalidLegacy);

        using var cache = NewCache(path);

        Assert.Equal(CacheFileState.MigrationFailed, cache.Status.State);
        Assert.Contains("cache", cache.Status.Action, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(invalidLegacy, File.ReadAllText(path));
        Assert.NotNull(cache.Status.BackupPath);
        Assert.Equal(invalidLegacy, File.ReadAllText(cache.Status.BackupPath!));
        Assert.Throws<CacheUnavailableException>(() => cache.Load());
        Assert.Throws<CacheUnavailableException>(() => cache.Add([Item()]));
    }

    [Fact]
    public void ReplacementThatFailsReloadValidation_NeverReplacesLegacyFile()
    {
        var path = TempPath();
        var legacyJson = JsonSerializer.Serialize(new[]
        {
            new LegacyEventDto(
                Guid.CreateVersion7(),
                InputEventType.KeyDown,
                65,
                DateTimeOffset.Parse("2026-08-11T00:00:00Z"))
        });
        File.WriteAllText(path, legacyJson);
        var invalidCurrentFormat = new JsonCacheFileFormat<InputEventItem, CurrentEventDto>(
            version: 1,
            item => new CurrentEventDto(item.Id, item.EventType, -1, item.Timestamp),
            item => item.Code < 0
                ? throw new JsonException("replacement validation failed")
                : new InputEventItem
                {
                    Id = item.Id,
                    EventType = item.EventType,
                    Code = item.Code,
                    Timestamp = item.Timestamp
                });

        using var cache = new JsonFileCache<InputEventItem>(
            path, 100_000, invalidCurrentFormat, [LegacyMigration()]);

        Assert.Equal(CacheFileState.MigrationFailed, cache.Status.State);
        Assert.Equal(legacyJson, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.NotNull(cache.Status.BackupPath);
        Assert.Equal(legacyJson, File.ReadAllText(cache.Status.BackupPath!));
    }

    [Fact]
    public void Replace_IsAtomicFromCallerPerspective_AndSurvivesRestart()
    {
        var path = TempPath();
        using (var cache = NewCache(path))
        {
            cache.Add([Item(1), Item(2), Item(3)]);
            cache.Replace([Item(8), Item(9)]);
            Assert.Equal([8, 9], cache.Load().Select(item => item.Code));
        }

        using var restarted = NewCache(path);
        Assert.Equal([8, 9], restarted.Load().Select(item => item.Code));
    }

    [Fact]
    public void EmptyAddAndClear_PreserveExpectedCacheBehavior()
    {
        using var cache = NewCache();
        cache.Add([]);
        Assert.Empty(cache.Load());

        cache.Add([Item()]);
        cache.Clear();
        Assert.Empty(cache.Load());
    }

    [Fact]
    public void Add_OverCapacity_RejectsWithoutDiscardingPreviouslyRetainedItems()
    {
        using var cache = NewCache(TempPath(), maxItems: 3);
        cache.Add([Item(1), Item(2), Item(3)]);
        Assert.Throws<IOException>(() => cache.Add([Item(4)]));
        Assert.Equal([1, 2, 3], cache.Load().Select(item => item.Code));
    }

    private sealed record CurrentEventDto(
        Guid Id,
        InputEventType EventType,
        short Code,
        DateTimeOffset Timestamp);

    private sealed record LegacyEventDto(
        Guid EventId,
        InputEventType Kind,
        short LegacyCode,
        DateTimeOffset OccurredAt);
}
