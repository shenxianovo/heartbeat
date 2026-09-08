using Heartbeat.Core.DTOs.Input;
using Heartbeat.Server.Data;
using Heartbeat.Server.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class InputEventServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device
        {
            OwnerId = "user-1",
            HardwareId = "hw-1",
            DeviceName = "Test PC"
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
    }

    private static InputEventItem Item(
        InputEventType type,
        short code,
        DateTimeOffset ts,
        string codeSet = InputCodeSets.WindowsVirtualKeyV1) => new()
    {
        Id = Guid.CreateVersion7(),
        EventType = type,
        CodeSet = codeSet,
        Code = code,
        Timestamp = ts
    };

    [Fact]
    public async Task SaveAsync_InsertsAllEvents()
    {
        using var db = CreateDbContext();
        var svc = new InputEventService(db);

        var now = DateTimeOffset.UtcNow;
        var request = new InputEventUploadRequest
        {
            Events =
            [
                Item(InputEventType.KeyDown, 65, now),
                Item(InputEventType.MouseButton, 1, now.AddMilliseconds(10)),
                Item(InputEventType.MouseScroll, 2, now.AddMilliseconds(20)),
            ]
        };

        await svc.SaveAsync(_deviceId, request);

        Assert.Equal(3, await db.InputEvents.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_IsIdempotent_WhenSameBatchUploadedTwice()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new InputEventUploadRequest
        {
            Events =
            [
                Item(InputEventType.KeyDown, 65, now),
                Item(InputEventType.KeyDown, 66, now.AddMilliseconds(10)),
            ]
        };

        // 第一次上传
        using (var db = CreateDbContext())
        {
            await new InputEventService(db).SaveAsync(_deviceId, request);
        }

        // 重传同一批（相同 Id）
        using (var db = CreateDbContext())
        {
            await new InputEventService(db).SaveAsync(_deviceId, request);
        }

        using (var db = CreateDbContext())
        {
            Assert.Equal(2, await db.InputEvents.CountAsync());
        }
    }

    [Fact]
    public async Task SaveAsync_DedupsWithinBatch()
    {
        using var db = CreateDbContext();
        var svc = new InputEventService(db);

        var dup = Item(InputEventType.KeyDown, 65, DateTimeOffset.UtcNow);
        var request = new InputEventUploadRequest
        {
            Events = [dup, dup]
        };

        await svc.SaveAsync(_deviceId, request);

        Assert.Equal(1, await db.InputEvents.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_PersistsFieldsCorrectly()
    {
        using var db = CreateDbContext();
        var svc = new InputEventService(db);

        var ts = DateTimeOffset.UtcNow;
        var item = Item(InputEventType.MouseScroll, 1, ts);
        await svc.SaveAsync(_deviceId, new InputEventUploadRequest { Events = [item] });

        var saved = await db.InputEvents.SingleAsync();
        Assert.Equal(item.Id, saved.Id);
        Assert.Equal(_deviceId, saved.DeviceId);
        Assert.Equal(InputEventType.MouseScroll, saved.EventType);
        Assert.Equal(InputCodeSets.WindowsVirtualKeyV1, saved.CodeSet);
        Assert.Equal((short)1, saved.Code);
    }

    [Fact]
    public async Task SaveAsync_EmptyBatch_DoesNothing()
    {
        using var db = CreateDbContext();
        var svc = new InputEventService(db);

        await svc.SaveAsync(_deviceId, new InputEventUploadRequest { Events = [] });

        Assert.Equal(0, await db.InputEvents.CountAsync());
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(false, "other-user", false)]
    [InlineData(false, "user-1", true)]
    public async Task PublicCounts_RespectsVisibilityAndHalfOpenWindow(bool isPublic, string? viewer, bool visible)
    {
        using var db = CreateDbContext();
        db.Users.Add(new User { Id = "user-1", Username = "alice", IsPublic = isPublic });
        await db.SaveChangesAsync();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var end = start.AddMinutes(30);
        var service = new InputEventService(db);
        await service.SaveAsync(_deviceId, new InputEventUploadRequest { Events = [
            Item(InputEventType.MouseButton, 1, start), Item(InputEventType.MouseButton, 1, end),
        ] });
        var context = new DefaultHttpContext();
        if (viewer != null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", viewer)]));
        var controller = new PublicUserController(new UserService(db), null!, null!, null!, null!, service, null!,
            new CurrentUserService(new HttpContextAccessor { HttpContext = context }));
        var result = await controller.GetInputCounts("alice", _deviceId, start, end);
        if (visible) Assert.Equal(1, result.Value!.MouseLeft);
        else Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetCounts_AggregatesByTypeAndCode()
    {
        var now = DateTimeOffset.UtcNow;
        using (var db = CreateDbContext())
        {
            await new InputEventService(db).SaveAsync(_deviceId, new InputEventUploadRequest
            {
                Events =
                [
                    Item(InputEventType.KeyDown, 65, now),
                    Item(InputEventType.KeyDown, 66, now.AddMilliseconds(1)),
                    Item(InputEventType.KeyDown, 67, now.AddMilliseconds(2)),
                    Item(InputEventType.MouseButton, 1, now.AddMilliseconds(3)),
                    Item(InputEventType.MouseButton, 1, now.AddMilliseconds(4)),
                    Item(InputEventType.MouseButton, 2, now.AddMilliseconds(5)),
                    Item(InputEventType.MouseButton, 3, now.AddMilliseconds(6)),
                    Item(InputEventType.MouseScroll, 1, now.AddMilliseconds(7)),
                    Item(InputEventType.MouseScroll, 2, now.AddMilliseconds(8)),
                    Item(InputEventType.MouseScroll, 2, now.AddMilliseconds(9)),
                ]
            });
        }

        using (var db = CreateDbContext())
        {
            var counts = await new InputEventService(db).GetCountsAsync("user-1", null, null, null);

            Assert.Equal(3, counts.KeyboardTotal);
            Assert.Equal(2, counts.MouseLeft);
            Assert.Equal(1, counts.MouseRight);
            Assert.Equal(1, counts.MouseMiddle);
            Assert.Equal(1, counts.ScrollUp);
            Assert.Equal(2, counts.ScrollDown);
        }
    }

    [Fact]
    public async Task GetCounts_FiltersByTimeRange()
    {
        var now = DateTimeOffset.UtcNow;
        using (var db = CreateDbContext())
        {
            await new InputEventService(db).SaveAsync(_deviceId, new InputEventUploadRequest
            {
                Events =
                [
                    Item(InputEventType.KeyDown, 65, now.AddHours(-2)),  // 范围外
                    Item(InputEventType.KeyDown, 66, now.AddMinutes(-30)), // 范围内
                    Item(InputEventType.KeyDown, 67, now.AddMinutes(-10)), // 范围内
                ]
            });
        }

        using (var db = CreateDbContext())
        {
            var counts = await new InputEventService(db)
                .GetCountsAsync("user-1", null, now.AddHours(-1), now);

            Assert.Equal(2, counts.KeyboardTotal);
        }
    }

    [Fact]
    public async Task GetCounts_OnlyCountsOwnerDevices()
    {
        var now = DateTimeOffset.UtcNow;
        // 另一个用户的设备
        long otherDeviceId;
        using (var db = CreateDbContext())
        {
            var other = new Device { OwnerId = "user-2", HardwareId = "hw-2", DeviceName = "Other" };
            db.Devices.Add(other);
            await db.SaveChangesAsync();
            otherDeviceId = other.Id;

            await new InputEventService(db).SaveAsync(_deviceId, new InputEventUploadRequest
            {
                Events = [Item(InputEventType.KeyDown, 65, now)]
            });
            await new InputEventService(db).SaveAsync(otherDeviceId, new InputEventUploadRequest
            {
                Events = [Item(InputEventType.KeyDown, 66, now), Item(InputEventType.KeyDown, 67, now.AddMilliseconds(1))]
            });
        }

        using (var db = CreateDbContext())
        {
            var counts = await new InputEventService(db).GetCountsAsync("user-1", null, null, null);
            Assert.Equal(1, counts.KeyboardTotal);
        }
    }

    [Fact]
    public async Task GetCounts_EmptyData_ReturnsZeros()
    {
        using var db = CreateDbContext();
        var counts = await new InputEventService(db).GetCountsAsync("user-1", null, null, null);

        Assert.Equal(0, counts.KeyboardTotal);
        Assert.Equal(0, counts.MouseLeft);
        Assert.Equal(0, counts.ScrollDown);
    }

    [Fact]
    public async Task GetKeyFrequency_GroupsByCode_OnlyKeyboard()
    {
        var now = DateTimeOffset.UtcNow;
        using (var db = CreateDbContext())
        {
            await new InputEventService(db).SaveAsync(_deviceId, new InputEventUploadRequest
            {
                Events =
                [
                    Item(InputEventType.KeyDown, 65, now),                  // A x3
                    Item(InputEventType.KeyDown, 65, now.AddMilliseconds(1)),
                    Item(InputEventType.KeyDown, 65, now.AddMilliseconds(2)),
                    Item(InputEventType.KeyDown, 66, now.AddMilliseconds(3)), // B x1
                    Item(InputEventType.MouseButton, 1, now.AddMilliseconds(4)), // 不算
                    Item(InputEventType.MouseScroll, 1, now.AddMilliseconds(5)), // 不算
                ]
            });
        }

        using (var db = CreateDbContext())
        {
            var freq = await new InputEventService(db).GetKeyFrequencyAsync("user-1", null, null, null);

            Assert.Equal(2, freq.Keys.Count);
            // 按 count 降序
            Assert.Equal((short)InputKeyPosition.KeyA, freq.Keys[0].Code);
            Assert.Equal(3, freq.Keys[0].Count);
            Assert.Equal((short)InputKeyPosition.KeyB, freq.Keys[1].Code);
            Assert.Equal(1, freq.Keys[1].Count);
        }
    }

    [Fact]
    public async Task GetKeyFrequency_FiltersByTimeRange()
    {
        var now = DateTimeOffset.UtcNow;
        using (var db = CreateDbContext())
        {
            await new InputEventService(db).SaveAsync(_deviceId, new InputEventUploadRequest
            {
                Events =
                [
                    Item(InputEventType.KeyDown, 65, now.AddHours(-2)),     // 范围外
                    Item(InputEventType.KeyDown, 65, now.AddMinutes(-30)),  // 范围内
                ]
            });
        }

        using (var db = CreateDbContext())
        {
            var freq = await new InputEventService(db)
                .GetKeyFrequencyAsync("user-1", null, now.AddHours(-1), now);

            Assert.Single(freq.Keys);
            Assert.Equal(1, freq.Keys[0].Count);
        }
    }

    [Fact]
    public async Task GetKeyFrequency_UsesHalfOpenInstantWindowEndpoints()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var end = start.AddHours(1);
        using (var db = CreateDbContext())
        {
            await new InputEventService(db).SaveAsync(_deviceId, new InputEventUploadRequest
            {
                Events =
                [
                    Item(InputEventType.KeyDown, 65, start),
                    Item(InputEventType.KeyDown, 65, start.AddMinutes(30)),
                    Item(InputEventType.KeyDown, 66, end),
                ]
            });
        }

        using (var db = CreateDbContext())
        {
            var freq = await new InputEventService(db)
                .GetKeyFrequencyAsync("user-1", null, start, end);

            var key = Assert.Single(freq.Keys);
            Assert.Equal((short)InputKeyPosition.KeyA, key.Code);
            Assert.Equal(2, key.Count);
        }
    }

    [Fact]
    public async Task GetKeyFrequency_OnlyOwnerDevices()
    {
        var now = DateTimeOffset.UtcNow;
        using (var db = CreateDbContext())
        {
            var other = new Device { OwnerId = "user-2", HardwareId = "hw-2", DeviceName = "Other" };
            db.Devices.Add(other);
            await db.SaveChangesAsync();

            await new InputEventService(db).SaveAsync(_deviceId, new InputEventUploadRequest
            {
                Events = [Item(InputEventType.KeyDown, 65, now)]
            });
            await new InputEventService(db).SaveAsync(other.Id, new InputEventUploadRequest
            {
                Events = [Item(InputEventType.KeyDown, 65, now.AddMilliseconds(1))]
            });
        }

        using (var db = CreateDbContext())
        {
            var freq = await new InputEventService(db).GetKeyFrequencyAsync("user-1", null, null, null);
            Assert.Single(freq.Keys);
            Assert.Equal(1, freq.Keys[0].Count);
        }
    }

    [Fact]
    public async Task GetKeyFrequency_EmptyData_ReturnsEmpty()
    {
        using var db = CreateDbContext();
        var freq = await new InputEventService(db).GetKeyFrequencyAsync("user-1", null, null, null);
        Assert.Empty(freq.Keys);
    }

    [Fact]
    public async Task GetKeyFrequency_ProjectsLegacyAndPhysicalCodesToSameKey_WithoutMutatingRawRows()
    {
        var now = DateTimeOffset.UtcNow;
        using (var db = CreateDbContext())
        {
            await new InputEventService(db).SaveAsync(_deviceId, new InputEventUploadRequest
            {
                Events =
                [
                    Item(InputEventType.KeyDown, 65, now, InputCodeSets.WindowsVirtualKeyV1),
                    Item(InputEventType.KeyDown, (short)InputKeyPosition.KeyA, now.AddMilliseconds(1),
                        InputCodeSets.HeartbeatKeyPositionV1)
                ]
            });
        }

        using (var db = CreateDbContext())
        {
            var freq = await new InputEventService(db).GetKeyFrequencyAsync("user-1", null, null, null);
            var key = Assert.Single(freq.Keys);
            Assert.Equal((short)InputKeyPosition.KeyA, key.Code);
            Assert.Equal(2, key.Count);

            var raw = await db.InputEvents.OrderBy(item => item.Timestamp).ToListAsync();
            Assert.Equal(InputCodeSets.WindowsVirtualKeyV1, raw[0].CodeSet);
            Assert.Equal((short)65, raw[0].Code);
            Assert.Equal(InputCodeSets.HeartbeatKeyPositionV1, raw[1].CodeSet);
            Assert.Equal((short)InputKeyPosition.KeyA, raw[1].Code);
        }
    }

    [Theory]
    [InlineData(0x0D)] // main Enter vs NumpadEnter
    [InlineData(0x10)] // generic Shift has no side
    [InlineData(0x11)] // generic Control has no side
    [InlineData(0x12)] // generic Alt has no side
    [InlineData(0x24)] // navigation cluster vs numpad with NumLock off
    [InlineData(0x25)]
    [InlineData(0x2D)]
    [InlineData(0x2E)]
    public void LegacyAmbiguousVk_DoesNotGuessPhysicalPosition(short code)
    {
        Assert.False(InputKeyProjection.TryProject(
            InputCodeSets.WindowsVirtualKeyV1,
            code,
            out _));
    }

    [Fact]
    public async Task SaveAsync_RejectsInvalidPhysicalCode_ButKeepsUnmappedLegacyVkTruthful()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateDbContext();
        var service = new InputEventService(db);

        await service.SaveAsync(_deviceId, new InputEventUploadRequest
        {
            Events = [Item(InputEventType.KeyDown, 255, now, InputCodeSets.WindowsVirtualKeyV1)]
        });

        await Assert.ThrowsAsync<InputEventIngestContractException>(() => service.SaveAsync(
            _deviceId,
            new InputEventUploadRequest
            {
                Events = [Item(InputEventType.KeyDown, 255, now, InputCodeSets.HeartbeatKeyPositionV1)]
            }));
        Assert.Single(await db.InputEvents.ToListAsync());
    }
}
