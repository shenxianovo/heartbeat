import { describe, expect, it } from 'vitest'
import type { SegmentSnapshot } from '../src/fold'
import {
  createBrowserDelivery,
  defaultBrowserDeliverySession,
  emptyBrowserDeliveryDurableState,
  type BrowserDelivery,
  type BrowserDeliveryDurableState,
  type BrowserDeliverySessionState,
  type BrowserDeliveryStore,
  type BrowserHubAdapter,
  type BrowserProtocolDeliveryRequest,
} from '../src/delivery'
import {
  snapshotRevision,
  type BrowserProtocolSession,
  type BrowserPublishAttempt,
  type ProtocolUploadResult,
} from '../src/protocol'

const PORT = 24_820
const ACTIVATION_ID = '0198d5e8-30cb-7d54-bab1-250087147e4c'
const STREAM_ID = '0198d5e2-e0d4-7b30-9da7-342ee261bf62'

function snapshot(index = 1, end = '2026-08-25T08:01:00.000Z'): SegmentSnapshot {
  return {
    id: `0198d5eb-fc31-7d7b-8bf0-${index.toString(16).padStart(12, '0')}`,
    source: 'browser',
    identityKey: `https://example.com/${index}`,
    title: `Page ${index}`,
    startTime: '2026-08-25T08:00:00.000Z',
    endTime: end,
    isFinal: false,
    attributes: {
      url: `https://example.com/${index}`,
      domain: 'example.com',
      site: 'example.com',
      windowId: 7,
    },
  }
}

function session(): BrowserProtocolSession {
  return {
    port: PORT,
    activationId: ACTIVATION_ID,
    leaseToken: 'lease',
    streamId: STREAM_ID,
    specRevision: 3,
    expiresAt: '2026-08-25T08:02:00.000Z',
    limits: { maxFactsPerBatch: 500, maxBatchBytes: 1_048_576 },
    flushPeriodMilliseconds: 30_000,
  }
}

class MemoryStore implements BrowserDeliveryStore {
  durable = emptyBrowserDeliveryDurableState()
  transient = defaultBrowserDeliverySession()
  failDurableWrites = 0

  async loadDurable(): Promise<BrowserDeliveryDurableState> {
    return structuredClone(this.durable)
  }

  async saveDurable(state: BrowserDeliveryDurableState): Promise<void> {
    if (this.failDurableWrites > 0) {
      this.failDurableWrites -= 1
      throw new Error('simulated storage interruption')
    }
    this.durable = structuredClone(state)
  }

  async loadSession(): Promise<BrowserDeliverySessionState> {
    return structuredClone(this.transient)
  }

  async saveSession(state: BrowserDeliverySessionState): Promise<void> {
    this.transient = structuredClone(state)
  }

  restartBrowser(): void {
    this.transient = defaultBrowserDeliverySession()
  }
}

class MemoryHub implements BrowserHubAdapter {
  compatiblePort: number | null = PORT
  protocolCalls: BrowserProtocolDeliveryRequest[] = []
  onProtocol: (request: BrowserProtocolDeliveryRequest) => Promise<ProtocolUploadResult> =
    async (request) => acknowledged(request)

  async findCompatibleHub(): Promise<number | null> {
    return this.compatiblePort
  }

  async deliverProtocol(request: BrowserProtocolDeliveryRequest): Promise<ProtocolUploadResult> {
    this.protocolCalls.push(request)
    return this.onProtocol(request)
  }

}

function delivery(
  store: MemoryStore,
  hub: MemoryHub,
  clock: { now: number } = { now: 1_000_000 },
): BrowserDelivery {
  return createBrowserDelivery({
    store,
    hub,
    loadAppIdentityKey: async () => 'win:msedge',
    loadBasePort: async () => PORT,
    loadExternalHostIdentity: async () => 'host-a',
    now: () => clock.now,
    warn: () => {},
  })
}

async function acknowledged(
  request: BrowserProtocolDeliveryRequest,
  statuses: Array<'committed' | 'rejected' | 'retry'> = request.snapshots.map(() => 'committed'),
): Promise<ProtocolUploadResult> {
  await request.applySpec({ enabled: true, flushPeriodMilliseconds: 30_000 })
  const acknowledgedRevisions: Record<string, number> = {}
  const rejectedRevisions: Record<string, number> = {}
  request.snapshots.forEach((item, index) => {
    if (statuses[index] === 'committed') acknowledgedRevisions[item.id] = snapshotRevision(item)
    if (statuses[index] === 'rejected') rejectedRevisions[item.id] = snapshotRevision(item)
  })
  return {
    kind: 'acked',
    acknowledgedIds: Object.keys(acknowledgedRevisions),
    acknowledgedRevisions,
    rejectedRevisions,
    session: session(),
  }
}

describe('BrowserDelivery interface', () => {
  it('keeps pending Facts without opening an Activation when App identity is unknown', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const module = createBrowserDelivery({
      store, hub,
      loadAppIdentityKey: async () => undefined,
      loadBasePort: async () => PORT,
      loadExternalHostIdentity: async () => 'host-a',
    })
    await module.enqueue([snapshot()])
    await module.deliveryCycle()
    expect(hub.protocolCalls).toHaveLength(0)
    expect(Object.values((await store.loadDurable()).queue)).toHaveLength(1)
  })

  it('owns enqueue, delivery, ACK convergence, and still maintains an empty Activation', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const module = delivery(store, hub)
    const item = snapshot()

    await module.enqueue([item])
    await module.deliveryCycle()

    expect(store.durable.queue).toEqual({})
    expect(store.transient.protocolSession?.activationId).toBe(ACTIVATION_ID)
    expect(hub.protocolCalls[0].externalHostIdentity).toBe('host-a')

    store.restartBrowser()
    await delivery(store, hub).deliveryCycle()
    expect(hub.protocolCalls.at(-1)?.snapshots).toEqual([])
  })

  it('serializes concurrent enqueue calls inside the module', async () => {
    const store = new MemoryStore()
    const module = delivery(store, new MemoryHub())

    await Promise.all([module.enqueue([snapshot(1)]), module.enqueue([snapshot(2)])])

    expect(Object.keys(store.durable.queue)).toEqual([snapshot(1).id, snapshot(2).id])
  })

  it('does not let an ACK for an older Revision delete a newer queued Revision', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const module = delivery(store, hub)
    const old = snapshot()
    const grown = snapshot(1, '2026-08-25T08:03:00.000Z')
    await module.enqueue([old])
    hub.onProtocol = async (request) => {
      store.durable.queue[old.id] = grown
      return acknowledged(request)
    }

    await module.deliveryCycle()

    expect(store.durable.queue[old.id]).toEqual(grown)
  })

  it('converges mixed ACK, reject, and retry behind the interface', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const clock = { now: 1_000_000 }
    const module = delivery(store, hub, clock)
    const [committed, rejected, retry] = [snapshot(1), snapshot(2), snapshot(3)]
    await module.enqueue([committed, rejected, retry])
    hub.onProtocol = async (request) => {
      const result = await acknowledged(request, ['committed', 'rejected', 'retry'])
      const nextPublishAttempt: BrowserPublishAttempt = {
        activationId: ACTIVATION_ID,
        messageId: '0198d5eb-fc31-7d7b-8bf0-000000000099',
        snapshots: [retry],
      }
      return { ...result, retryAfterMilliseconds: 4_000, nextPublishAttempt }
    }

    await module.deliveryCycle()

    expect(Object.values(store.durable.queue)).toEqual([retry])
    expect(store.durable.deadLetters).toEqual([rejected])
    expect(store.transient.publishAttempt?.snapshots).toEqual([retry])
    await module.deliveryCycle()
    expect(hub.protocolCalls).toHaveLength(1)
    clock.now += 4_000
    await module.deliveryCycle()
    expect(hub.protocolCalls).toHaveLength(2)
  })

  it('recovers a persisted Publish attempt after Service Worker termination', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const item = snapshot()
    const attempt: BrowserPublishAttempt = {
      activationId: ACTIVATION_ID,
      messageId: '0198d5eb-fc31-7d7b-8bf0-000000000088',
      snapshots: [item],
    }
    const first = delivery(store, hub)
    await first.enqueue([item])
    hub.onProtocol = async (request) => {
      await request.persistPublishAttempt(attempt)
      return { kind: 'unavailable', publishAttempt: attempt, session: session() }
    }
    await first.deliveryCycle()

    let recovered: BrowserPublishAttempt | undefined
    hub.onProtocol = async (request) => {
      recovered = request.previousPublishAttempt
      return acknowledged(request)
    }
    await delivery(store, hub, { now: 1_030_000 }).deliveryCycle()

    expect(recovered).toEqual(attempt)
    expect(store.durable.queue).toEqual({})
  })

  it('rebuilds transport attempts after browser restart while retaining Fact identity', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const item = snapshot()
    const module = delivery(store, hub)
    await module.enqueue([item])
    hub.onProtocol = async (request) => {
      const attempt: BrowserPublishAttempt = {
        activationId: ACTIVATION_ID,
        messageId: '0198d5eb-fc31-7d7b-8bf0-000000000066',
        snapshots: request.snapshots,
      }
      await request.persistPublishAttempt(attempt)
      return { kind: 'unavailable', publishAttempt: attempt, session: session() }
    }
    await module.deliveryCycle()

    store.restartBrowser()
    hub.onProtocol = async (request) => {
      expect(request.previousPublishAttempt).toBeUndefined()
      expect(request.snapshots.map((fact) => fact.id)).toEqual([item.id])
      return acknowledged(request)
    }
    await delivery(store, hub, { now: 1_030_000 }).deliveryCycle()

    expect(store.durable.queue).toEqual({})
  })

  it('continues enqueue during backoff and retries only when the window expires', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const clock = { now: 1_000_000 }
    const module = delivery(store, hub, clock)
    hub.compatiblePort = null

    await module.deliveryCycle()
    await module.enqueue([snapshot()])
    clock.now += 29_999
    await module.deliveryCycle()
    expect(store.durable.queue).toHaveProperty(snapshot().id)
    clock.now += 1
    await module.deliveryCycle()
    expect(store.transient.backoff.fails).toBe(2)
  })

  it('turns overflow and interrupted durable writes into persistent Stream Gaps', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const module = delivery(store, hub)
    const full = Array.from({ length: 5_001 }, (_, index) => snapshot(index + 1))

    await module.enqueue(full)
    expect(Object.keys(store.durable.queue)).toHaveLength(5_000)
    expect(store.durable.pendingGaps[0]).toMatchObject({
      reason: 'buffer_overflow',
      estimatedFactsLost: 1,
    })

    const interruptedStore = new MemoryStore()
    interruptedStore.failDurableWrites = 1
    await delivery(interruptedStore, hub).enqueue([snapshot()])
    expect(interruptedStore.durable.queue).toEqual({})
    expect(interruptedStore.durable.pendingGaps[0].estimatedFactsLost).toBe(1)
  })

  it('reports the oldest Gap before Facts and removes only its explicit ACK', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const module = delivery(store, hub)
    const item = snapshot()
    store.durable.pendingGaps = [
      { gapId: '0198d5eb-fc30-7d7b-8bf0-000000000001', start: item.startTime, end: item.endTime, reason: 'buffer_overflow', estimatedFactsLost: 1 },
      { gapId: '0198d5eb-fc30-7d7b-8bf0-000000000002', start: item.startTime, end: item.endTime, reason: 'buffer_overflow', estimatedFactsLost: 2 },
    ]
    await module.enqueue([item])
    hub.onProtocol = async (request) => {
      expect(request.pendingGap?.estimatedFactsLost).toBe(1)
      const attempted = {
        ...request.pendingGap!,
        activationId: ACTIVATION_ID,
        messageId: '0198d5eb-fc31-7d7b-8bf0-000000000077',
      }
      await request.persistGapAttempt(attempted)
      return { ...(await acknowledged(request)), gapAcknowledged: true }
    }

    await module.deliveryCycle()

    expect(store.durable.pendingGaps).toHaveLength(1)
    expect(store.durable.pendingGaps[0].estimatedFactsLost).toBe(2)
  })

  it('persists known-disabled policy across browser restart and re-enables from modern Spec', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const module = delivery(store, hub)
    hub.onProtocol = async () => ({ kind: 'disabled' })

    await expect(module.deliveryCycle()).resolves.toMatchObject({ enabled: false })
    store.restartBrowser()
    expect(await delivery(store, hub).policy()).toMatchObject({ enabled: false })

    hub.onProtocol = async (request) => acknowledged(request)
    await expect(delivery(store, hub).deliveryCycle()).resolves.toMatchObject({ enabled: true })
  })

  it('retains the outbox when Collector Protocol is unavailable', async () => {
    const store = new MemoryStore()
    const hub = new MemoryHub()
    const module = delivery(store, hub)
    const item = snapshot()
    await module.enqueue([item])
    hub.onProtocol = async () => ({ kind: 'unavailable' })

    await module.deliveryCycle()
    expect(store.durable.queue).toHaveProperty(item.id)
    expect(hub.protocolCalls).toHaveLength(1)
  })
})
