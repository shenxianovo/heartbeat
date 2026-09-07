import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SegmentSnapshot } from '../src/fold'
import { ChromeBrowserDeliveryStore, loadExternalHostIdentity } from '../src/delivery-chrome'
import { defaultBrowserDeliverySession } from '../src/delivery'

class MemoryStorageArea {
  values: Record<string, unknown>

  constructor(initial: Record<string, unknown> = {}) {
    this.values = structuredClone(initial)
  }

  async get(keys?: string | string[] | Record<string, unknown> | null) {
    if (keys === undefined || keys === null) return structuredClone(this.values)
    const names = typeof keys === 'string'
      ? [keys]
      : Array.isArray(keys) ? keys : Object.keys(keys)
    return Object.fromEntries(names
      .filter((key) => this.values[key] !== undefined)
      .map((key) => [key, structuredClone(this.values[key])]))
  }

  async set(items: Record<string, unknown>) {
    Object.assign(this.values, structuredClone(items))
  }

  async remove(keys: string | string[]) {
    for (const key of typeof keys === 'string' ? [keys] : keys) delete this.values[key]
  }
}

function installChrome(
  local: Record<string, unknown> = {},
  session: Record<string, unknown> = {},
) {
  const localArea = new MemoryStorageArea(local)
  const sessionArea = new MemoryStorageArea(session)
  vi.stubGlobal('chrome', { storage: { local: localArea, session: sessionArea } })
  return { localArea, sessionArea }
}

const legacySnapshot = {
  id: '0198d5eb-fc31-7d7b-8bf0-000000000001',
  source: 'browser' as const,
  identityKey: 'https://example.com/page',
  appName: 'msedge',
  title: 'Example',
  startTime: '2026-08-25T08:00:00.000Z',
  endTime: '2026-08-25T08:01:00.000Z',
  attributes: {
    url: 'https://example.com/page',
    domain: 'example.com',
    site: 'example.com',
    windowId: 7,
  },
}

afterEach(() => vi.unstubAllGlobals())

describe('ChromeBrowserDeliveryStore adapter contract', () => {
  it('starts a new worker Activation while retaining durable Facts and Profile identity', async () => {
    installChrome()
    const first = new ChromeBrowserDeliveryStore()
    const identity = await loadExternalHostIdentity()
    await first.saveSession({
      ...defaultBrowserDeliverySession(),
      activationAttempt: {
        helloMessageId: 'old', initializedMessageId: 'old', streamsMessageId: 'old', readyMessageId: 'old',
      },
    })
    const durable = await first.loadDurable()
    durable.queue[legacySnapshot.id] = { ...legacySnapshot, isFinal: true }
    await first.saveDurable(durable)
    const restarted = new ChromeBrowserDeliveryStore()
    expect((await restarted.loadSession()).activationAttempt).toBeUndefined()
    expect(Object.keys((await restarted.loadDurable()).queue)).toEqual([legacySnapshot.id])
    expect(await loadExternalHostIdentity()).toBe(identity)
  })

  it('persists one External Host identity in local storage and creates a new one after reset', async () => {
    const { localArea, sessionArea } = installChrome()

    const first = await loadExternalHostIdentity()
    sessionArea.values = {}
    const afterBrowserRestart = await loadExternalHostIdentity()
    localArea.values = {}
    const afterExtensionReset = await loadExternalHostIdentity()

    expect(afterBrowserRestart).toBe(first)
    expect(afterExtensionReset).not.toBe(first)
    expect(first).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('recovers the existing Chrome layout without leaking legacy fields', async () => {
    const { localArea } = installChrome({
      pendingSegments: { [legacySnapshot.id]: legacySnapshot },
      browserCollectorPendingGap: {
        start: legacySnapshot.startTime,
        end: legacySnapshot.endTime,
        reason: 'buffer_overflow',
        estimatedFactsLost: 2,
      },
      browserCollectorDeadLetters: [legacySnapshot],
    }, {
      browserCollectorDesiredEnabled: false,
      browserCollectorFlushPeriodMs: 60_000,
      backoff: { fails: 2, nextAttemptAt: 123_000 },
    })
    const store = new ChromeBrowserDeliveryStore()

    const durable = await store.loadDurable()
    const recovered = durable.queue[legacySnapshot.id]

    expect(recovered).not.toHaveProperty('appName')
    expect(recovered).toMatchObject({ isFinal: false })
    expect(durable.pendingGaps).toHaveLength(1)
    expect(durable.pendingGaps[0].gapId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7/)
    expect((localArea.values.browserCollectorPendingGap as Array<{ gapId: string }>)[0].gapId)
      .toBe(durable.pendingGaps[0].gapId)
    expect(durable.policy).toEqual({ enabled: false, flushPeriodMilliseconds: 60_000 })
    await expect(store.loadSession()).resolves.toMatchObject({
      backoff: { fails: 2, nextAttemptAt: 123_000 },
    })
  })

  it('persists collection policy in local storage across a browser restart', async () => {
    const { sessionArea } = installChrome()
    const store = new ChromeBrowserDeliveryStore()
    const durable = await store.loadDurable()
    durable.policy = { enabled: false, flushPeriodMilliseconds: 90_000 }
    await store.saveDurable(durable)

    sessionArea.values = {}

    await expect(new ChromeBrowserDeliveryStore().loadDurable()).resolves.toMatchObject({
      policy: { enabled: false, flushPeriodMilliseconds: 90_000 },
    })
  })

  it('round-trips session attempts and removes obsolete optional fields', async () => {
    installChrome()
    const store = new ChromeBrowserDeliveryStore()
    const state = {
      ...defaultBrowserDeliverySession(),
      hubPort: 24_821,
      publishAttempt: {
        activationId: '0198d5e8-30cb-7d54-bab1-250087147e4c',
        messageId: '0198d5eb-fc31-7d7b-8bf0-000000000010',
        snapshots: [{ ...legacySnapshot, isFinal: false } as SegmentSnapshot],
      },
    }
    await store.saveSession(state)
    await expect(store.loadSession()).resolves.toEqual(state)

    await store.saveSession(defaultBrowserDeliverySession())
    await expect(store.loadSession()).resolves.toEqual(defaultBrowserDeliverySession())
  })
})
