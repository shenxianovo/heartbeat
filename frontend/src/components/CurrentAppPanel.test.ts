// @vitest-environment happy-dom

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import CurrentAppPanel from './CurrentAppPanel.vue'

vi.mock('../api/index', () => ({
  fetchAppIcon: vi.fn(async () => null),
}))

function mountPanel(overrides: Record<string, unknown> = {}) {
  return mount(CurrentAppPanel, {
    props: {
      username: 'alice',
      isToday: true,
      presences: [],
      ...overrides,
    },
    global: {
      stubs: {
        Card: { template: '<section><slot /></section>' },
        RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
      },
    },
  })
}

describe('CurrentAppPanel', () => {
  it('hides the whole card when no device is online', () => {
    const wrapper = mountPanel({
      presences: [{
        deviceId: 1,
        deviceName: 'Old laptop',
        isOnline: false,
        currentApp: null,
        currentAppId: null,
        currentAppKey: null,
        currentAppIdentityKey: null,
        lastSeen: new Date(2026, 7, 12),
      }],
    })

    expect(wrapper.text()).not.toContain('当前使用')
    expect(wrapper.text()).not.toContain('Old laptop')
  })

  it('renders only online devices in the multi-device view', () => {
    const wrapper = mountPanel({
      presences: [
        {
          deviceId: 1,
          deviceName: 'Online laptop',
          isOnline: true,
          currentApp: 'Visual Studio Code',
          currentAppId: 1,
          currentAppKey: 'vscode',
          currentAppIdentityKey: 'mac:com.microsoft.vscode',
          lastSeen: new Date(),
        },
        {
          deviceId: 2,
          deviceName: 'Online desktop',
          isOnline: true,
          currentApp: 'Terminal',
          currentAppId: 2,
          currentAppKey: 'terminal',
          currentAppIdentityKey: 'win:terminal',
          lastSeen: new Date(),
        },
        {
          deviceId: 3,
          deviceName: 'Offline desktop',
          isOnline: false,
          currentApp: null,
          currentAppId: null,
          currentAppKey: null,
          currentAppIdentityKey: null,
          lastSeen: new Date(2026, 7, 12),
        },
      ],
    })

    expect(wrapper.text()).toContain('Online laptop')
    expect(wrapper.text()).toContain('Online desktop')
    expect(wrapper.text()).not.toContain('Offline desktop')
  })

  it('shows the device name when exactly one device is online', () => {
    const wrapper = mountPanel({
      presences: [{
        deviceId: 1,
        deviceName: 'MacBook Pro',
        isOnline: true,
        currentApp: 'Visual Studio Code',
        currentAppId: 1,
        currentAppKey: 'vscode',
        currentAppIdentityKey: 'mac:com.microsoft.vscode',
        lastSeen: new Date(),
      }],
    })

    expect(wrapper.text()).toContain('Visual Studio Code')
    expect(wrapper.text()).toContain('MacBook Pro')
  })
})
