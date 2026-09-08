// @vitest-environment happy-dom

import { defineComponent, ref } from 'vue'
import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchPublicDeviceStatus } from '../api/index'
import { useDeviceStatus } from './useDeviceStatus'

vi.mock('../api/index', () => ({
  fetchPublicDeviceStatus: vi.fn(),
  toApiError: vi.fn(() => ({ kind: 'parse' })),
}))

describe('useDeviceStatus refresh generation isolation', () => {
  beforeEach(() => vi.clearAllMocks())

  it('does not let a slower status response from an older generation replace the current presence', async () => {
    type Status = Awaited<ReturnType<typeof fetchPublicDeviceStatus>>
    let resolveOld!: (value: Status) => void
    let resolveNew!: (value: Status) => void
    vi.mocked(fetchPublicDeviceStatus)
      .mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve }))
      .mockImplementationOnce(() => new Promise(resolve => { resolveNew = resolve }))

    let status!: ReturnType<typeof useDeviceStatus>
    const selectedDevice = ref(7)
    const wrapper = mount(defineComponent({
      setup() {
        status = useDeviceStatus(
          'alice',
          ref([
            { id: 7, name: 'Old laptop' },
            { id: 8, name: 'New laptop' },
          ] as never[]),
          selectedDevice,
          ref(true),
        )
        return () => null
      },
    }))

    const oldLoad = status.load()
    selectedDevice.value = 8
    const newLoad = status.load()
    resolveNew({ isOnline: true, currentApp: 'New app' } as Status)
    await newLoad
    resolveOld({ isOnline: true, currentApp: 'Old app' } as Status)
    await oldLoad

    expect(status.onlinePresences.value[0]?.currentApp).toBe('New app')
    wrapper.unmount()
  })
})
