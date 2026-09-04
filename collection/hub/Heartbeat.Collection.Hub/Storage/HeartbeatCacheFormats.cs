using Heartbeat.Core;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Storage;

/// <summary>
/// Production cache schemas. Domain DTOs never serve as persistence DTOs: both the current
/// versioned envelope and the unversioned predecessor have explicit shapes and mappings.
/// Segment v2 is the strict AppIdentity schema; input v2 adds the explicit CodeSet contract.
/// </summary>
public static class HeartbeatCacheFormats
{
    public static IJsonCacheFileFormat<ActivitySegmentItem> SegmentVersion2() =>
        new JsonCacheFileFormat<ActivitySegmentItem, SegmentCacheItemV2>(
            version: 2,
            ToSegmentV2,
            FromSegmentV2);

    public static IReadOnlyList<IJsonCacheMigration<ActivitySegmentItem>> SegmentMigrations() =>
    [
        JsonCacheMigration<ActivitySegmentItem, LegacySegmentCacheItem>.FromUnversionedArray(
            targetVersion: 2,
            item => ToStrictSegment(
                item.Id, item.Source, item.IdentityKey, item.AppIdentityKey, item.AppName,
                item.Title, item.StartTime, item.EndTime, item.Attributes)),
        JsonCacheMigration<ActivitySegmentItem, SegmentCacheItemV1>.FromVersion(
            sourceVersion: 1,
            targetVersion: 2,
            item => ToStrictSegment(
                item.Id, item.Source, item.IdentityKey, item.AppIdentityKey, item.AppName,
                item.Title, item.StartTime, item.EndTime, item.Attributes))
    ];

    public static IJsonCacheFileFormat<InputEventItem> InputEventVersion2() =>
        new JsonCacheFileFormat<InputEventItem, InputEventCacheItemV2>(
            version: 2,
            item => new InputEventCacheItemV2(item.Id, item.EventType, item.CodeSet, item.Code, item.Timestamp),
            item => new InputEventItem
            {
                Id = item.Id,
                EventType = item.EventType,
                CodeSet = item.CodeSet,
                Code = item.Code,
                Timestamp = item.Timestamp
            });

    public static IReadOnlyList<IJsonCacheMigration<InputEventItem>> InputEventMigrations() =>
    [
        JsonCacheMigration<InputEventItem, LegacyInputEventCacheItem>.FromUnversionedArray(
            targetVersion: 2,
            item => new InputEventItem
            {
                Id = item.Id,
                EventType = item.EventType,
                CodeSet = InputCodeSets.WindowsVirtualKeyV1,
                Code = item.Code,
                Timestamp = item.Timestamp
            }),
        JsonCacheMigration<InputEventItem, InputEventCacheItemV1>.FromVersion(
            sourceVersion: 1,
            targetVersion: 2,
            item => new InputEventItem
            {
                Id = item.Id,
                EventType = item.EventType,
                CodeSet = InputCodeSets.WindowsVirtualKeyV1,
                Code = item.Code,
                Timestamp = item.Timestamp
            })
    ];

    public static IJsonCacheFileFormat<Guid> InputEventDeliveryReceiptVersion1() =>
        new JsonCacheFileFormat<Guid, string>(
            version: 1,
            item => item.ToString("D"),
            Guid.Parse);

    private static SegmentCacheItemV2 ToSegmentV2(ActivitySegmentItem item) => new(
        item.Id,
        item.Source,
        item.IdentityKey,
        item.AppIdentityKey,
        item.AppDisplayName,
        item.Title,
        item.StartTime,
        item.EndTime,
        item.Attributes?.Clone());

    private static ActivitySegmentItem FromSegmentV2(SegmentCacheItemV2 item) => new()
    {
        Id = item.Id,
        Source = item.Source,
        IdentityKey = item.IdentityKey,
        AppIdentityKey = item.AppIdentityKey,
        AppDisplayName = item.AppDisplayName,
        Title = item.Title,
        StartTime = item.StartTime,
        EndTime = item.EndTime,
        Attributes = item.Attributes?.Clone()
    };

    private static ActivitySegmentItem ToStrictSegment(
        Guid id,
        string source,
        string identityKey,
        string? appIdentityKey,
        string? legacyAppName,
        string? title,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        JsonElement? attributes)
    {
        var strictIdentity = !string.IsNullOrWhiteSpace(appIdentityKey)
            ? AppIdentityKeys.Normalize(appIdentityKey)
            : !string.IsNullOrWhiteSpace(legacyAppName)
                ? AppIdentityKeys.FromLegacyWindowsAppName(legacyAppName)
                : null;

        return new ActivitySegmentItem
        {
            Id = id,
            Source = source,
            // ADR-035: preserve the original evidence/identity guard byte-for-byte.
            IdentityKey = identityKey,
            AppIdentityKey = strictIdentity,
            AppDisplayName = legacyAppName,
            AppName = null,
            Title = title,
            StartTime = startTime,
            EndTime = endTime,
            Attributes = attributes?.Clone()
        };
    }

    private sealed record SegmentCacheItemV2(
        Guid Id,
        string Source,
        string IdentityKey,
        string? AppIdentityKey,
        string? AppDisplayName,
        string? Title,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime,
        JsonElement? Attributes);

    private sealed record SegmentCacheItemV1(
        Guid Id,
        string Source,
        string IdentityKey,
        string? AppIdentityKey,
        string? AppName,
        string? Title,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime,
        JsonElement? Attributes);

    private sealed record LegacySegmentCacheItem(
        Guid Id,
        string Source,
        string IdentityKey,
        string? AppIdentityKey,
        string? AppName,
        string? Title,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime,
        JsonElement? Attributes);

    private sealed record InputEventCacheItemV2(
        Guid Id,
        InputEventType EventType,
        string CodeSet,
        short Code,
        DateTimeOffset Timestamp);

    private sealed record InputEventCacheItemV1(
        Guid Id,
        InputEventType EventType,
        short Code,
        DateTimeOffset Timestamp);

    private sealed record LegacyInputEventCacheItem(
        Guid Id,
        InputEventType EventType,
        short Code,
        DateTimeOffset Timestamp);
}
