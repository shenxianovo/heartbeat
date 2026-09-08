// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { expect, it, vi } from 'vitest'
import { AppUsageResponse } from '../api/client'
import ActivityTimeline from './ActivityTimeline.vue'
import { resolveCalendarContext } from '../calendar/localCalendarWindow'

vi.mock('../stores/auth', () => ({ authStore: { isAuthenticated: false, username: { value: '' } } }))

it.each([false, true])('preserves the explored range even before initial data arrives (%s), and initializes a new day', async hasInitialData => {
  const dayWindow = resolveCalendarContext('2026-09-07', { timeZone: 'Asia/Shanghai' }).day
  const usage = [new AppUsageResponse({ appId: 1, deviceId: 1, startTime: new Date('2026-09-07T01:00:00Z'), endTime: new Date('2026-09-07T09:00:00Z') })]
  const wrapper = mount(ActivityTimeline, {
    props: { username: 'alice', usageData: [], appNameMap: new Map([[1, 'Code']]), dayWindow, isToday: false, devices: [], isAllDevices: false },
    global: { stubs: { AppIcon: true } },
  })
  if (hasInitialData) await wrapper.setProps({ usageData: usage })
  const range = () => wrapper.get('.cursor-grab[style]').attributes('style')
  const initial = range()
  await wrapper.get('.select-none').trigger('wheel', { deltaX: 100, deltaY: 0 })
  const explored = range()
  expect(explored).not.toBe(initial)
  await wrapper.setProps({ usageData: [...usage], dayWindow: { ...dayWindow } })
  expect(range()).toBe(explored)
  await wrapper.setProps({ dayWindow: resolveCalendarContext('2026-09-06', { timeZone: 'Asia/Shanghai' }).day, usageData: [] })
  expect(range()).not.toBe(explored)
  wrapper.unmount()
})
