import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { CalendarContext } from '../calendar/localCalendarWindow'
import { useReports } from './useReports'
import {
  fetchPublicDailyReport,
  fetchPublicWeeklyReport,
  fetchPublicUsage,
} from '../api/index'

vi.mock('../api/index', () => ({
  fetchPublicDailyReport: vi.fn(async () => ({ date: '2026-03-08', apps: [] })),
  fetchPublicWeeklyReport: vi.fn(async () => ({ apps: [] })),
  fetchPublicUsage: vi.fn(async () => []),
  toApiError: vi.fn(() => ({ kind: 'parse' })),
}))

const context: CalendarContext = Object.freeze({
  day: Object.freeze({
    version: 1,
    kind: 'day',
    localDate: '2026-03-08',
    timeZone: 'America/New_York',
    start: '2026-03-08T05:00:00Z',
    endExclusive: '2026-03-09T04:00:00Z',
  }),
  week: Object.freeze({
    version: 1,
    kind: 'week',
    localDate: '2026-03-08',
    timeZone: 'America/New_York',
    start: '2026-03-02T05:00:00Z',
    endExclusive: '2026-03-09T04:00:00Z',
  }),
  isToday: false,
  displayLabel: '2026-03-08 · America/New_York (UTC-05:00 → UTC-04:00)',
  correlationIdentity: 'refresh-1',
})

describe('useReports Local Calendar Window', () => {
  beforeEach(() => vi.clearAllMocks())

  it('reuses one captured context for Daily, Weekly, and the generic usage adapter', async () => {
    const reports = useReports('alice', ref(0), ref(context))

    await Promise.all([reports.loadDaily(), reports.loadWeekly(), reports.loadUsage()])

    expect(fetchPublicDailyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: context.day,
    })
    expect(fetchPublicUsage).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      start: context.day.start,
      end: context.day.endExclusive,
    })
    expect(fetchPublicWeeklyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: context.week,
    })
  })

  it('changes only device filtering when switching from all-device to single-device', async () => {
    const selectedDevice = ref(0)
    const reports = useReports('alice', selectedDevice, ref(context))

    await reports.loadUsage()
    selectedDevice.value = 42
    await reports.loadUsage()

    expect(vi.mocked(fetchPublicUsage).mock.calls).toEqual([
      ['alice', { deviceId: 0, start: context.day.start, end: context.day.endExclusive }],
      ['alice', { deviceId: 42, start: context.day.start, end: context.day.endExclusive }],
    ])
  })

  it('clips Usage-derived dashboard durations to the captured day endpoints', async () => {
    vi.mocked(fetchPublicUsage).mockResolvedValueOnce([
      {
        deviceId: 7,
        appId: 1,
        appName: 'Code',
        startTime: new Date('2026-03-08T04:00:00Z'),
        endTime: new Date('2026-03-08T06:00:00Z'),
        durationSeconds: 7200,
      },
      {
        deviceId: 7,
        appId: 1,
        appName: 'Code',
        startTime: new Date('2026-03-09T03:00:00Z'),
        endTime: new Date('2026-03-09T05:00:00Z'),
        durationSeconds: 7200,
      },
    ] as Awaited<ReturnType<typeof fetchPublicUsage>>)
    const reports = useReports('alice', ref(0), ref(context))

    await reports.loadUsage()

    expect(reports.onlineSeconds.value).toBe(7200)
    expect(reports.perDeviceSeconds.value).toEqual([
      { deviceId: 7, usageSeconds: 7200, awaySeconds: 0 },
    ])
  })

  it('does not let an older refresh overwrite the current Daily Report', async () => {
    type DailyReport = Awaited<ReturnType<typeof fetchPublicDailyReport>>
    let resolveOld!: (value: DailyReport) => void
    let resolveNew!: (value: DailyReport) => void
    vi.mocked(fetchPublicDailyReport)
      .mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve }))
      .mockImplementationOnce(() => new Promise(resolve => { resolveNew = resolve }))

    const current = ref(context)
    const reports = useReports('alice', ref(0), current)
    const oldRequest = reports.loadDaily()
    current.value = Object.freeze({ ...context, correlationIdentity: 'refresh-2' })
    const newRequest = reports.loadDaily()

    resolveNew({ date: '2026-03-08', apps: [{ appId: 2, appName: 'new', durationSeconds: 2 }] } as DailyReport)
    await newRequest
    resolveOld({ date: '2026-03-08', apps: [{ appId: 1, appName: 'old', durationSeconds: 1 }] } as DailyReport)
    await oldRequest

    expect(reports.appSummaries.value.map(app => app.appName)).toEqual(['new'])
  })
})

it('retains a same-window report on refresh failure, but clears it when the day or device changes', async () => {
  const current = ref(context)
  const device = ref(0)
  const reports = useReports('alice', device, current)
  vi.mocked(fetchPublicDailyReport).mockResolvedValueOnce({ apps: [{ appId: 1, appName: 'Yesterday', durationSeconds: 120 }] } as never)
  await reports.loadDaily()
  vi.mocked(fetchPublicDailyReport).mockRejectedValueOnce(new Error('offline'))
  await reports.loadDaily()
  expect(reports.appSummaries.value[0]?.appName).toBe('Yesterday')
  current.value = { ...context, day: { ...context.day, start: '2026-03-09T04:00:00Z', endExclusive: '2026-03-10T04:00:00Z' } }
  vi.mocked(fetchPublicDailyReport).mockRejectedValueOnce(new Error('offline'))
  await reports.loadDaily()
  expect(reports.appSummaries.value).toEqual([])
  vi.mocked(fetchPublicDailyReport).mockResolvedValueOnce({ apps: [{ appId: 1, appName: 'Today', durationSeconds: 120 }] } as never)
  await reports.loadDaily()
  device.value = 42
  vi.mocked(fetchPublicDailyReport).mockRejectedValueOnce(new Error('offline'))
  await reports.loadDaily()
  expect(reports.appSummaries.value).toEqual([])
})
