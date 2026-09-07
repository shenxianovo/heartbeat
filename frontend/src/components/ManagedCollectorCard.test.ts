// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ManagedCollectorCard from './ManagedCollectorCard.vue'

const api = vi.hoisted(() => ({
  installManagedCollector: vi.fn(async () => undefined),
  uninstallManagedCollector: vi.fn(async () => undefined),
  retryManagedCollector: vi.fn(async () => undefined),
  submitCollectorAuthorization: vi.fn(async () => undefined),
}))

vi.mock('../api/index', () => api)

const stubs = {
  Card: { template: '<section><slot /></section>' },
  Button: { props: ['type', 'disabled'], template: '<button :type="type" :disabled="disabled"><slot /></button>' },
}

describe('ManagedCollectorCard', () => {
  beforeEach(() => vi.clearAllMocks())

  it('installs a catalog item using only its package id', async () => {
    const wrapper = mount(ManagedCollectorCard, {
      props: { collector: {
        packageId: 'heartbeat.collector.reference',
        displayName: 'Reference Collector',
        summary: 'Generic fixture',
        latestVersion: '1.0.0',
        isInstalled: false,
        phase: 'NotInstalled',
      } },
      global: { stubs },
    })

    await wrapper.get('button').trigger('click')
    await flushPromises()

    expect(api.installManagedCollector).toHaveBeenCalledWith('heartbeat.collector.reference')
    expect(wrapper.emitted('changed')).toHaveLength(1)
    expect(wrapper.text()).not.toContain('http')
  })

  it('can resubmit the same authorization challenge after explicit cancellation', async () => {
    const wrapper = mount(ManagedCollectorCard, {
      props: { collector: {
        packageId: 'heartbeat.collector.reference',
        displayName: 'Reference Collector',
        summary: 'Generic fixture',
        isInstalled: true,
        installedVersion: '1.0.0',
        collectorInstanceId: '0198d5df-5df3-70a1-937d-68a7d64623e3',
        phase: 'WaitingForAuthorization',
        authorization: {
          interactionId: '0198d5df-5df3-70a1-937d-68a7d64623e4',
          kind: 'Credentials' as const,
          title: 'Sign in',
          fields: [{ name: 'token', label: 'Token', isSecret: true }],
        },
      } },
      global: { stubs },
    })
    await wrapper.get('input').setValue('secret')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(api.submitCollectorAuthorization).toHaveBeenCalledWith(
      '0198d5df-5df3-70a1-937d-68a7d64623e3',
      '0198d5df-5df3-70a1-937d-68a7d64623e4',
      { token: 'secret' },
    )
    await wrapper.setProps({ operation: {
      operationId: 'authorization-operation', packageId: 'heartbeat.collector.reference',
      kind: 'SubmitAuthorization', phase: 'Cancelled', isTerminal: true,
    } })
    expect((wrapper.get('button[type="submit"]').element as HTMLButtonElement).disabled).toBe(false)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(api.submitCollectorAuthorization).toHaveBeenCalledTimes(2)

  })
})
