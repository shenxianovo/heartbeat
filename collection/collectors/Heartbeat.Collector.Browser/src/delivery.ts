import type { SegmentSnapshot } from './fold'
import { uuidv7 } from './ids'
import {
  snapshotRevision,
  type BrowserActivationAttempt,
  type BrowserPendingGap,
  type BrowserProtocolSession,
  type BrowserPublishAttempt,
  type ProtocolUploadResult,
} from './protocol'

const DEFAULT_FLUSH_PERIOD_MS = 30_000
const BACKOFF_BASE_MS = 30_000
const BACKOFF_MAX_MS = 10 * 60_000
const MAX_QUEUED = 5_000
const MAX_DEAD_LETTERS = 100

export interface BrowserCollectionPolicy {
  enabled: boolean
  flushPeriodMilliseconds: number
}

/**
 * Browser delivery 的外部 seam。调用方只表达待交付快照和一次调度触发；持久化、
 * Activation、ACK 收敛与兼容路径都属于模块实现。
 */
export interface BrowserDelivery {
  policy(): Promise<BrowserCollectionPolicy>
  enqueue(snapshots: SegmentSnapshot[]): Promise<void>
  deliveryCycle(): Promise<BrowserCollectionPolicy>
}

/** @internal Chrome 与内存 storage adapter 共享的模块私有状态。 */
export interface BrowserDeliveryDurableState {
  queue: Record<string, SegmentSnapshot>
  pendingGaps: BrowserPendingGap[]
  deadLetters: SegmentSnapshot[]
  policy: BrowserCollectionPolicy
}

/** @internal 浏览器会话内可恢复、浏览器重启后可安全重建的状态。 */
export interface BrowserDeliverySessionState {
  backoff: { fails: number; nextAttemptAt: number }
  hubPort?: number
  protocolSession?: BrowserProtocolSession
  activationAttempt?: BrowserActivationAttempt
  publishAttempt?: BrowserPublishAttempt
}

/** @internal chrome.storage 与测试内存实现所在的 seam。 */
export interface BrowserDeliveryStore {
  loadDurable(): Promise<BrowserDeliveryDurableState>
  saveDurable(state: BrowserDeliveryDurableState): Promise<void>
  loadSession(): Promise<BrowserDeliverySessionState>
  saveSession(state: BrowserDeliverySessionState): Promise<void>
}

export interface BrowserProtocolDeliveryRequest {
  port: number
  appIdentityKey: string | undefined
  externalHostIdentity: string
  snapshots: SegmentSnapshot[]
  previousSession?: BrowserProtocolSession
  previousActivationAttempt?: BrowserActivationAttempt
  previousPublishAttempt?: BrowserPublishAttempt
  persistActivationAttempt(attempt: BrowserActivationAttempt): Promise<void>
  persistPublishAttempt(attempt: BrowserPublishAttempt): Promise<void>
  applySpec(spec: BrowserCollectionPolicy): Promise<void>
  pendingGap?: BrowserPendingGap
  persistGapAttempt(gap: BrowserPendingGap): Promise<void>
}

/** @internal loopback HTTP 与测试内存实现所在的 seam。 */
export interface BrowserHubAdapter {
  findCompatibleHub(basePort: number, targetPort: number): Promise<number | null>
  deliverProtocol(request: BrowserProtocolDeliveryRequest): Promise<ProtocolUploadResult>
}

export interface BrowserDeliveryDependencies {
  store: BrowserDeliveryStore
  hub: BrowserHubAdapter
  loadAppIdentityKey(): Promise<string | undefined>
  loadBasePort(): Promise<number>
  loadExternalHostIdentity(): Promise<string>
  now?(): number
  warn?(message: string, error?: unknown): void
}

export function createBrowserDelivery(dependencies: BrowserDeliveryDependencies): BrowserDelivery {
  const now = dependencies.now ?? Date.now
  const warn = dependencies.warn ?? ((message: string, error?: unknown) => console.warn(message, error ?? ''))
  let deliveryChain: Promise<unknown> = Promise.resolve()

  function serialized<T>(operation: () => Promise<T>): Promise<T> {
    const next = deliveryChain.then(operation, operation)
    deliveryChain = next.catch(() => {})
    return next
  }

  async function policy(): Promise<BrowserCollectionPolicy> {
    return (await dependencies.store.loadDurable()).policy
  }

  async function enqueueImplementation(snapshots: SegmentSnapshot[]): Promise<void> {
    if (snapshots.length === 0) return
    const durable = await dependencies.store.loadDurable()
    const { queue, overflow } = enqueueBounded(durable.queue, snapshots)
    const next = {
      ...durable,
      queue,
      pendingGaps: appendBufferGap(durable.pendingGaps, overflow),
    }
    try {
      await dependencies.store.saveDurable(next)
    } catch (error) {
      warn('[heartbeat] outbox 写入失败，记录 Stream Gap', error)
      await dependencies.store.saveDurable({
        ...durable,
        pendingGaps: appendBufferGap(durable.pendingGaps, snapshots),
      })
    }
  }

  async function deliveryCycleImplementation(): Promise<BrowserCollectionPolicy> {
    let session = await dependencies.store.loadSession()
    let currentPolicy = (await dependencies.store.loadDurable()).policy
    const appIdentityKey = await dependencies.loadAppIdentityKey()
    if (appIdentityKey === undefined) return currentPolicy
    const attemptAt = now()
    if (attemptAt < session.backoff.nextAttemptAt) return currentPolicy

    const basePort = await dependencies.loadBasePort()
    const targetPort = session.hubPort ?? basePort
    const compatiblePort = await dependencies.hub.findCompatibleHub(basePort, targetPort)
    if (compatiblePort === null) {
      session = failWithBackoff(session, attemptAt)
      await dependencies.store.saveSession(session)
      return currentPolicy
    }
    if (compatiblePort !== session.hubPort) {
      session = { ...session, hubPort: compatiblePort }
      await dependencies.store.saveSession(session)
    }

    const durable = await dependencies.store.loadDurable()
    const snapshots = Object.values(durable.queue)
    let reportedGap = durable.pendingGaps[0]

    const protocolResult = await dependencies.hub.deliverProtocol({
      port: compatiblePort,
      appIdentityKey,
      externalHostIdentity: await dependencies.loadExternalHostIdentity(),
      snapshots,
      previousSession: session.protocolSession,
      previousActivationAttempt: session.activationAttempt,
      previousPublishAttempt: relevantPublishAttempt(session.publishAttempt, durable.queue),
      persistActivationAttempt: async (attempt) => {
        session = { ...session, activationAttempt: attempt }
        await dependencies.store.saveSession(session)
      },
      persistPublishAttempt: async (attempt) => {
        session = { ...session, publishAttempt: attempt }
        await dependencies.store.saveSession(session)
      },
      applySpec: async (spec) => {
        currentPolicy = spec
        await persistPolicy(dependencies.store, currentPolicy)
      },
      pendingGap: reportedGap,
      persistGapAttempt: async (attempt) => {
        reportedGap = attempt
        const latest = await dependencies.store.loadDurable()
        await dependencies.store.saveDurable({
          ...latest,
          pendingGaps: replaceFirstGap(latest.pendingGaps, durable.pendingGaps[0], attempt),
        })
      },
    })

    if (protocolResult.kind === 'acked') {
      await convergeProtocolAcknowledgement(
        dependencies.store,
        protocolResult,
        protocolResult.gapAcknowledged === true ? reportedGap : undefined,
        warn,
      )
      session = {
        ...session,
        protocolSession: protocolResult.session,
        activationAttempt: undefined,
        publishAttempt: protocolResult.nextPublishAttempt,
        backoff: protocolResult.retryAfterMilliseconds === undefined
          ? noBackoff()
          : { fails: 0, nextAttemptAt: attemptAt + protocolResult.retryAfterMilliseconds },
      }
      await dependencies.store.saveSession(session)
      return currentPolicy
    }

    if (protocolResult.kind === 'disabled') {
      session = {
        ...session,
        activationAttempt: undefined,
        publishAttempt: undefined,
      }
      await dependencies.store.saveSession(session)
      currentPolicy = { ...currentPolicy, enabled: false }
      await persistPolicy(dependencies.store, currentPolicy)
      return currentPolicy
    }

    if (protocolResult.kind === 'unavailable') {
      if (protocolResult.gapAcknowledged === true && reportedGap !== undefined) {
        await removeAcknowledgedGap(dependencies.store, reportedGap)
      }
      session = {
        ...session,
        activationAttempt: protocolResult.activationAttempt,
        publishAttempt: protocolResult.publishAttempt,
        protocolSession: protocolResult.publishAttempt === undefined
          ? undefined
          : protocolResult.session,
      }
      session = failWithBackoff(session, attemptAt)
      await dependencies.store.saveSession(session)
      return currentPolicy
    }

    session = {
      ...session,
      protocolSession: undefined,
      activationAttempt: undefined,
      publishAttempt: undefined,
    }
    await dependencies.store.saveSession(session)
    session = failWithBackoff(session, attemptAt)
    await dependencies.store.saveSession(session)
    return currentPolicy
  }

  return {
    policy,
    enqueue: (snapshots) => serialized(() => enqueueImplementation(snapshots)),
    deliveryCycle: () => serialized(deliveryCycleImplementation),
  }
}

function enqueueBounded(
  current: Record<string, SegmentSnapshot>,
  snapshots: SegmentSnapshot[],
): { queue: Record<string, SegmentSnapshot>; overflow: SegmentSnapshot[] } {
  const queue = { ...current }
  const overflow: SegmentSnapshot[] = []
  let queuedCount = Object.keys(queue).length
  for (const snapshot of snapshots) {
    if (queue[snapshot.id] === undefined && queuedCount >= MAX_QUEUED) {
      overflow.push(snapshot)
      continue
    }
    if (queue[snapshot.id] === undefined) queuedCount += 1
    queue[snapshot.id] = snapshot
  }
  return { queue, overflow }
}

function appendBufferGap(
  gaps: BrowserPendingGap[],
  snapshots: SegmentSnapshot[],
): BrowserPendingGap[] {
  if (snapshots.length === 0) return gaps
  return [...gaps, {
    gapId: uuidv7(),
    start: snapshots.reduce((earliest, item) => item.startTime < earliest ? item.startTime : earliest, snapshots[0].startTime),
    end: snapshots.reduce((latest, item) => item.endTime > latest ? item.endTime : latest, snapshots[0].endTime),
    reason: 'buffer_overflow',
    estimatedFactsLost: snapshots.length,
  }]
}

async function convergeProtocolAcknowledgement(
  store: BrowserDeliveryStore,
  result: Extract<ProtocolUploadResult, { kind: 'acked' }>,
  acknowledgedGap: BrowserPendingGap | undefined,
  warn: (message: string, error?: unknown) => void,
): Promise<void> {
  const durable = await store.loadDurable()
  const queue = { ...durable.queue }
  const rejected: SegmentSnapshot[] = []
  for (const [id, snapshot] of Object.entries(queue)) {
    const revision = snapshotRevision(snapshot)
    if (result.rejectedRevisions[id] === revision) {
      rejected.push(snapshot)
      delete queue[id]
    } else if (result.acknowledgedRevisions[id] === revision) {
      delete queue[id]
    }
  }
  if (rejected.length > 0) {
    warn(`[heartbeat] ${rejected.length} 条 Fact 被 Hub 永久拒绝，已移入诊断 dead-letter`)
  }
  await store.saveDurable({
    ...durable,
    queue,
    pendingGaps: acknowledgedGap === undefined
      ? durable.pendingGaps
      : removeGap(durable.pendingGaps, acknowledgedGap),
    deadLetters: [...durable.deadLetters, ...rejected].slice(-MAX_DEAD_LETTERS),
  })
}

async function removeAcknowledgedGap(
  store: BrowserDeliveryStore,
  acknowledged: BrowserPendingGap,
): Promise<void> {
  const durable = await store.loadDurable()
  await store.saveDurable({
    ...durable,
    pendingGaps: removeGap(durable.pendingGaps, acknowledged),
  })
}

function replaceFirstGap(
  gaps: BrowserPendingGap[],
  expected: BrowserPendingGap | undefined,
  replacement: BrowserPendingGap,
): BrowserPendingGap[] {
  if (expected === undefined) return gaps
  const index = gaps.findIndex((gap) => sameGap(gap, expected))
  if (index < 0) return gaps
  return gaps.map((gap, position) => position === index ? replacement : gap)
}

function removeGap(gaps: BrowserPendingGap[], acknowledged: BrowserPendingGap): BrowserPendingGap[] {
  const index = gaps.findIndex((gap) => sameGap(gap, acknowledged))
  return index < 0 ? gaps : gaps.filter((_, position) => position !== index)
}

function sameGap(left: BrowserPendingGap, right: BrowserPendingGap): boolean {
  return left.gapId === right.gapId
}

function relevantPublishAttempt(
  attempt: BrowserPublishAttempt | undefined,
  queue: Record<string, SegmentSnapshot>,
): BrowserPublishAttempt | undefined {
  if (attempt === undefined) return undefined
  return attempt.snapshots.some((snapshot) =>
    queue[snapshot.id] !== undefined &&
    snapshotRevision(queue[snapshot.id]) === snapshotRevision(snapshot),
  ) ? attempt : undefined
}

function noBackoff(): BrowserDeliverySessionState['backoff'] {
  return { fails: 0, nextAttemptAt: 0 }
}

function failWithBackoff(
  session: BrowserDeliverySessionState,
  now: number,
): BrowserDeliverySessionState {
  const fails = session.backoff.fails + 1
  const delay = Math.min(BACKOFF_BASE_MS * 2 ** (fails - 1), BACKOFF_MAX_MS)
  return { ...session, backoff: { fails, nextAttemptAt: now + delay } }
}

export const defaultBrowserDeliverySession = (): BrowserDeliverySessionState => ({
  backoff: noBackoff(),
})

export const emptyBrowserDeliveryDurableState = (): BrowserDeliveryDurableState => ({
  queue: {},
  pendingGaps: [],
  deadLetters: [],
  policy: { enabled: true, flushPeriodMilliseconds: DEFAULT_FLUSH_PERIOD_MS },
})

async function persistPolicy(
  store: BrowserDeliveryStore,
  policy: BrowserCollectionPolicy,
): Promise<void> {
  const durable = await store.loadDurable()
  await store.saveDurable({ ...durable, policy })
}
