// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import HubManagementView from './HubManagementView.vue'

const api = vi.hoisted(() => ({
  fetchManagedCollectors: vi.fn(),
  fetchManagedOperations: vi.fn(async () => []),
  cancelManagedOperation: vi.fn(),
  installManagedCollector: vi.fn(async () => undefined),
  uninstallManagedCollector: vi.fn(async () => undefined),
  retryManagedCollector: vi.fn(async () => undefined),
  submitCollectorAuthorization: vi.fn(async () => undefined),
}))

vi.mock('../api/index', () => api)

describe('HubManagementView', () => {
  beforeEach(() => vi.clearAllMocks())
  afterEach(() => vi.useRealTimers())

  it('lists generic catalog entries without Collector-specific production knowledge', async () => {
    api.fetchManagedCollectors.mockResolvedValue([{
      packageId: 'heartbeat.collector.reference',
      displayName: 'Reference Collector',
      summary: 'A generic Collector',
      latestVersion: '1.0.0',
      isInstalled: false,
      phase: 'NotInstalled',
    }])
    const wrapper = mount(HubManagementView, {
      global: { stubs: {
        Card: { template: '<section><slot /></section>' },
        Button: { template: '<button><slot /></button>' },
        RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
      } },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('Hub 管理')
    expect(wrapper.text()).toContain('Reference Collector')
    expect(wrapper.text()).toContain('安装')
    expect(wrapper.get('a[href="/settings"]').text()).toContain('返回设置')
    wrapper.unmount()
  })

  it('restores Host operations even while the Collector catalog cannot finish loading', async () => {
    vi.useFakeTimers()
    api.fetchManagedCollectors.mockReturnValue(new Promise(() => {}))
    api.fetchManagedOperations.mockResolvedValue([{
      operationId: 'operation-1', packageId: 'reference', kind: 'Install',
      phase: 'Running', isTerminal: false,
    }] as never)
    const wrapper = mount(HubManagementView, {
      global: { stubs: {
        Card: { template: '<section><slot /></section>' },
        Button: { props: ['type', 'disabled'], template: '<button :type="type" :disabled="disabled"><slot /></button>' },
        RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
      } },
    })
    await flushPromises()
    expect(wrapper.text()).toContain('安装中')
    const cancel = wrapper.findAll('button').find(button => button.text() === '取消')!
    await cancel.trigger('click')
    await flushPromises()
    expect(api.cancelManagedOperation).toHaveBeenCalledWith('operation-1')

    api.fetchManagedOperations.mockResolvedValue([{
      operationId: 'operation-1', packageId: 'reference', kind: 'Install',
      phase: 'Failed', isTerminal: true, failure: 'Registry unavailable',
    }] as never)
    await vi.advanceTimersByTimeAsync(5_000)
    await flushPromises()
    expect(wrapper.text()).toContain('Registry unavailable')
    wrapper.unmount()
    const calls = api.fetchManagedOperations.mock.calls.length
    await vi.advanceTimersByTimeAsync(10_000)
    expect(api.fetchManagedOperations).toHaveBeenCalledTimes(calls)
  })
})
