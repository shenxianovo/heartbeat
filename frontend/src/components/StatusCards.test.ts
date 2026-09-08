// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { expect, it, vi } from 'vitest'
import StatusCards from './StatusCards.vue'
import AppIcon from './AppIcon.vue'

vi.mock('../api/index', () => ({ fetchAppIcon: vi.fn(async () => null) }))

vi.mock('../composables/useHeartbeat', () => ({ formatDuration: (seconds: number) => `${seconds}s` }))

it('keeps deduplicated activity distinct from concurrent device totals and loading', async () => {
  const wrapper = mount(StatusCards, {
    props: {
      username: 'alice', isToday: true, isAlive: true, lastSeenStr: '5 分钟前', lastSeenTitle: '',
      loading: false, failed: false,
      appSummaries: [{ appId: 42, appName: 'Code', totalSeconds: 120 }],
      totalSeconds: 120, onlineSeconds: 60, awaySeconds: 30,
      hasConcurrentUse: true, isAllDevices: true, includeAway: false,
    },
  })
  expect(wrapper.findAll('[data-slot=card]')).toHaveLength(3)
  expect(wrapper.text()).toContain('本次存活')
  expect(wrapper.text()).toContain('今日最爱')
  expect(wrapper.text()).toContain('沉迷时长 120s')
  expect(wrapper.getComponent(AppIcon).props()).toMatchObject({ username: 'alice', appId: 42 })
  expect(wrapper.text()).toContain('死了吗还活着')
  expect(wrapper.get('.font-mono').text()).toBe('60s')
  expect(wrapper.text()).toContain('屏幕占用 120s')
  expect(wrapper.text()).toContain('另有空转 30s')
  await wrapper.setProps({ loading: true, appSummaries: [], onlineSeconds: 0, totalSeconds: 0, awaySeconds: 0 })
  expect(wrapper.text()).toContain('加载中…')
  expect(wrapper.text()).not.toContain('0m')
  await wrapper.setProps({ loading: false, failed: true })
  expect(wrapper.text()).toContain('—')
  await wrapper.setProps({ isAlive: false })
  expect(wrapper.text()).toContain('似了喵')
  expect(wrapper.text()).toContain('最后活跃 5 分钟前')
  wrapper.unmount()
})
