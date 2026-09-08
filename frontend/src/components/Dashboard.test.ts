// @vitest-environment happy-dom

import { flushPromises, shallowMount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  fetchPublicDailyReport,
  fetchPublicKeyFrequency,
  fetchPublicInputCounts,
  fetchPublicUsage,
  fetchPublicWeeklyReport,
} from '../api/index'
import { resolveCalendarContext } from '../calendar/localCalendarWindow'
import Dashboard from './Dashboard.vue'
import ActivityTimeline from './ActivityTimeline.vue'
import DatePicker from './DatePicker.vue'
import RecapCard from './RecapCard.vue'
import StrandQuestions from './StrandQuestions.vue'

const calendarState = vi.hoisted(() => ({
  timeZone: 'Asia/Shanghai',
  now: '2026-08-29T04:00:00Z',
  identity: 'ordinary-refresh',
}))

vi.mock('../calendar/localCalendarWindow', async importOriginal => {
  const original = await importOriginal<typeof import('../calendar/localCalendarWindow')>()
  return {
    ...original,
    resolveCalendarContext: vi.fn((localDate: string) => original.resolveCalendarContext(localDate, {
      timeZone: calendarState.timeZone,
      now: calendarState.now,
      correlationIdentity: () => calendarState.identity,
    })),
  }
})

vi.mock('../stores/auth', () => ({
  authStore: {
    isAuthenticated: true,
    username: { value: 'alice' },
    logout: vi.fn(),
    redirectToLogin: vi.fn(),
  },
}))

vi.mock('../api/index', () => ({
  fetchAdminAppCatalog: vi.fn(async () => ({ products: [] })),
  fetchMe: vi.fn(async () => ({ isAdmin: false })),
  fetchPublicApps: vi.fn(async () => []),
  fetchPublicDevices: vi.fn(async () => []),
  fetchPublicDeviceStatus: vi.fn(async () => ({})),
  fetchPublicDailyReport: vi.fn(async () => ({ apps: [] })),
  fetchPublicWeeklyReport: vi.fn(async () => ({ apps: [] })),
  fetchPublicUsage: vi.fn(async () => []),
  fetchPublicKeyFrequency: vi.fn(async () => []),
  fetchPublicInputCounts: vi.fn(async () => ({ mouseLeft: 12, mouseRight: 2, mouseMiddle: 1, scrollUp: 3, scrollDown: 4 })),
  toApiError: vi.fn(() => ({ kind: 'parse' })),
}))

const scenarios = [
  {
    name: 'ordinary day',
    systemTime: '2026-08-29T04:00:00Z',
    timeZone: 'Asia/Shanghai',
    now: '2026-08-29T04:00:00Z',
    identity: 'ordinary-refresh',
  },
  {
    name: 'fall-back transition day',
    systemTime: '2026-11-01T12:00:00Z',
    timeZone: 'America/New_York',
    now: '2026-11-01T12:00:00Z',
    identity: 'fall-back-refresh',
  },
]

describe('Dashboard Calendar Context orchestration', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.useFakeTimers()
  })

  afterEach(() => vi.useRealTimers())

  it.each(scenarios)('refreshes an end-to-end $name with one shared immutable Context', async scenario => {
    vi.setSystemTime(new Date(scenario.systemTime))
    Object.assign(calendarState, scenario)
    const wrapper = shallowMount(Dashboard, {
      props: { username: 'alice' },
      global: {
        stubs: { RouterLink: true },
      },
    })
    await flushPromises()

    expect(wrapper.find('details').exists()).toBe(false)
    expect(fetchPublicInputCounts).toHaveBeenCalled()
    expect(resolveCalendarContext).toHaveBeenCalledTimes(1)
    const context = wrapper.findComponent(RecapCard).props('calendarContext')
    expect(Object.isFrozen(context)).toBe(true)
    expect(context.correlationIdentity).toBe(scenario.identity)
    expect(context.day.timeZone).toBe(scenario.timeZone)
    expect(wrapper.findComponent(DatePicker).props('contextLabel')).toBe(context.displayLabel)
    expect(wrapper.findComponent(StrandQuestions).props('calendarContext')).toBe(context)
    expect(wrapper.findComponent(ActivityTimeline).props('dayWindow')).toBe(context.day)
    expect(wrapper.findComponent(ActivityTimeline).props('isToday')).toBe(context.isToday)

    expect(fetchPublicDailyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: context.day,
    })
    expect(fetchPublicWeeklyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: context.week,
    })
    expect(fetchPublicUsage).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      start: context.day.start,
      end: context.day.endExclusive,
    })
    expect(fetchPublicKeyFrequency).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      start: context.day.start,
      end: context.day.endExclusive,
    })

    wrapper.unmount()
  })
})
