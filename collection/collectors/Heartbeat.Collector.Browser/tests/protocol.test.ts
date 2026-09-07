import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SegmentSnapshot } from '../src/fold'
import {
  acknowledgedSnapshotIds,
  publishBrowserFacts,
  reportBrowserGap,
  snapshotRevision,
  toProtocolFact,
  uploadWithBrowserProtocol,
  type BrowserProtocolSession,
  type BrowserPendingGap,
} from '../src/protocol'

const packageReference = {
  packageId: 'heartbeat.collector.browser', packageVersion: '0.1.0',
  packageContentHash: `sha256:${'1'.repeat(64)}`,
  artifactId: 'browser.extension', artifactHash: `sha256:${'2'.repeat(64)}`,
}

beforeEach(() => vi.stubGlobal('chrome', { runtime: { getURL: (path: string) => `chrome-extension://fixture/${path}` } }))
function protocolFetch(handler: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) {
  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => String(input).startsWith('chrome-extension://')
    ? Promise.resolve(Response.json(packageReference)) : handler(input, init))
}

const snapshot = (id = '0198d5eb-fc31-7d7b-8bf0-c2d009ec8999'): SegmentSnapshot => ({
  id,
  source: 'browser',
  identityKey: 'https://example.com/docs',
  title: 'Docs',
  startTime: '2026-08-25T08:00:00.000Z',
  endTime: '2026-08-25T08:01:00.000Z',
  isFinal: false,
  attributes: { url: 'https://example.com/docs?q=1', domain: 'example.com', site: 'example.com', windowId: 7 },
})

afterEach(() => vi.unstubAllGlobals())

const ACTIVATION_ID = '0198d5e8-30cb-7d54-bab1-250087147e4c'
const STREAM_ID = '0198d5e2-e0d4-7b30-9da7-342ee261bf62'

const protocolResponse = (
  type: string,
  body: object,
  activationId?: string,
  replyTo?: string,
) => Response.json({
  protocol: type.startsWith('activation.accept') ? 'heartbeat.collector.bootstrap/1' : 'heartbeat.collector/1',
  type,
  messageId: '0198d5e8-30cc-743c-a3d6-ac61956f26b5',
  ...(activationId === undefined ? {} : { activationId }),
  ...(replyTo === undefined ? {} : { replyTo }),
  body,
})

describe('browser Collector Protocol outbox', () => {
  it('canonical Fact excludes App identity while preserving typed browser payload', () => {
    const fact = toProtocolFact(snapshot(), '0198d5e2-e0d4-7b30-9da7-342ee261bf62')!
    expect(fact.payload).toEqual({
      identityKey: 'https://example.com/docs',
      title: 'Docs',
      attributes: { url: 'https://example.com/docs?q=1', domain: 'example.com', site: 'example.com', windowId: 7 },
    })
    expect(fact.payload).not.toHaveProperty('appHint')
    expect(fact.revision).toBe(snapshotRevision(snapshot()))
    expect(fact.time.isFinal).toBe(false)
  })

  it('only explicitly acknowledged results select outbox entries for deletion', () => {
    const items = [snapshot(), snapshot('0198d5eb-fc31-7d7b-8bf0-c2d009ec8998')]
    expect(acknowledgedSnapshotIds(items, {
      results: [
        { index: 0, status: 'committed' },
        { index: 1, status: 'rejected' },
      ],
    })).toEqual([items[0].id])
  })

  it('old non-UUIDv7 cache remains unavailable without entering a legacy transport', async () => {
    await expect(uploadWithBrowserProtocol(
      24820,
      'win:msedge',
      'host-a',
      [snapshot('legacy-id')],
    )).resolves.toEqual({
      kind: 'unavailable',
    })
  })

  it('happy path negotiates Spec, opens Stream, and returns per-Fact ACK identities', async () => {
    const calls: { url: string; body: unknown }[] = []
    vi.stubGlobal('fetch', protocolFetch(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const request = init?.body ? JSON.parse(String(init.body)) : undefined
      calls.push({ url, body: request })
      if (url.endsWith('/hello')) return protocolResponse('activation.accepted', {
        activationId: ACTIVATION_ID,
        selectedProtocolMajor: 1,
        selectedCapabilities: { 'facts.segment': 1, 'diagnostics.stream-gap': 1 },
      }, undefined, request.messageId)
      if (url.endsWith('/initialize')) return protocolResponse('activation.initialize', {
        spec: { revision: 3, config: { value: { enabled: true, flushPeriodMs: 30_000 } } },
        limits: { maxFactsPerBatch: 500, maxBatchBytes: 1_048_576 },
      }, ACTIVATION_ID)
      if (url.endsWith('/initialized')) return new Response(null, { status: 204 })
      if (url.endsWith('/streams')) return protocolResponse('streams.opened', {
        streams: { tabs: { streamId: STREAM_ID } },
      }, ACTIVATION_ID, request.messageId)
      if (url.endsWith('/ready')) return protocolResponse('activation.readyAck', {
        lease: { token: 'lease', expiresAt: '2026-08-25T08:01:00Z' },
      }, ACTIVATION_ID, request.messageId)
      return protocolResponse(
        'facts.ack',
        { results: [{ index: 0, status: 'committed' }] },
        ACTIVATION_ID,
        request.messageId,
      )
    }))

    const applySpec = vi.fn(async () => {})
    const result = await uploadWithBrowserProtocol(
      24820,
      'win:msedge',
      'host-a',
      [snapshot()],
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      applySpec,
    )

    expect(result.kind).toBe('acked')
    expect(applySpec).toHaveBeenCalledWith({ enabled: true, flushPeriodMilliseconds: 30_000 })
    if (result.kind === 'acked') expect(result.acknowledgedIds).toEqual([snapshot().id])
    expect(calls.every(call => call.url.includes('/v1/collector-protocol/external-host/'))).toBe(true)
    expect(calls.map((call) => call.url.split('/').at(-1))).toEqual([
      'hello', 'initialize', 'initialized', 'streams', 'ready', 'facts',
    ])
    expect((calls[0].body as { body: object }).body).toMatchObject({
        ...packageReference,
      appIdentityKey: 'win:msedge',
      externalHostIdentity: 'host-a',
    })
    expect((calls[5].body as { body: { facts: { payload: object }[] } }).body.facts[0].payload).not.toHaveProperty('appHint')
  })

  it('opens the Package-backed protocol session even when the outbox is empty', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', protocolFetch(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const request = init?.body ? JSON.parse(String(init.body)) : undefined
      calls.push(url)
      if (url.endsWith('/hello')) return protocolResponse('activation.accepted', {
        activationId: ACTIVATION_ID,
        selectedProtocolMajor: 1,
        selectedCapabilities: { 'facts.segment': 1, 'diagnostics.stream-gap': 1 },
      }, undefined, request.messageId)
      if (url.endsWith('/initialize')) return protocolResponse('activation.initialize', {
        spec: { revision: 3, config: { value: { enabled: true, flushPeriodMs: 30_000 } } },
        limits: { maxFactsPerBatch: 500, maxBatchBytes: 1_048_576 },
      }, ACTIVATION_ID)
      if (url.endsWith('/initialized')) return new Response(null, { status: 204 })
      if (url.endsWith('/streams')) return protocolResponse('streams.opened', {
        streams: { tabs: { streamId: STREAM_ID } },
      }, ACTIVATION_ID, request.messageId)
      return protocolResponse('activation.readyAck', {
        lease: { token: 'lease', expiresAt: '2026-08-25T08:01:00Z' },
      }, ACTIVATION_ID, request.messageId)
    }))

    const result = await uploadWithBrowserProtocol(24820, 'win:msedge', 'host-a', [])

    expect(result.kind).toBe('acked')
    if (result.kind === 'acked') expect(result.acknowledgedIds).toEqual([])
    expect(calls.map((url) => url.split('/').at(-1))).toEqual([
      'hello', 'initialize', 'initialized', 'streams', 'ready',
    ])
  })

  it('respects negotiated batch limits and reuses messageId after response loss', async () => {
    const messageIds: string[] = []
    const revisions: number[] = []
    let loseResponse = true
    vi.stubGlobal('fetch', protocolFetch(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as {
        messageId: string
        body: { facts: unknown[] }
      }
      messageIds.push(body.messageId)
      expect(body.body.facts).toHaveLength(1)
      revisions.push((body.body.facts[0] as { revision: number }).revision)
      if (loseResponse) {
        loseResponse = false
        const response = Response.json({})
        vi.spyOn(response, 'json').mockRejectedValue(new Error('lost response'))
        return response
      }
      return protocolResponse(
        'facts.ack',
        { results: [{ index: 0, status: 'duplicate' }] },
        session.activationId,
        body.messageId,
      )
    }))
    const session: BrowserProtocolSession = {
      port: 24820,
      activationId: '0198d5e8-30cb-7d54-bab1-250087147e4c',
      leaseToken: 'lease',
      streamId: '0198d5e2-e0d4-7b30-9da7-342ee261bf62',
      specRevision: 3,
      expiresAt: '2026-08-25T08:01:00Z',
      limits: { maxFactsPerBatch: 1, maxBatchBytes: 1_048_576 },
      flushPeriodMilliseconds: 30_000,
    }
    const items = [snapshot(), snapshot('0198d5eb-fc31-7d7b-8bf0-c2d009ec8998')]

    const lost = await publishBrowserFacts(session, items)
    expect(lost.kind).toBe('unavailable')
    const attempt = lost.kind === 'unavailable' ? lost.publishAttempt : undefined
    expect(attempt).toBeDefined()
    expect(attempt?.activationId).toBe(session.activationId)
    if (lost.kind === 'unavailable') expect(lost.session).toEqual(session)
    const grown = [{ ...items[0], endTime: '2026-08-25T08:02:00.000Z' }, items[1]]
    const replay = await publishBrowserFacts(session, grown, attempt)

    expect(replay.kind).toBe('acked')
    expect(messageIds[1]).toBe(messageIds[0])
    expect(revisions[1]).toBe(revisions[0])
    if (replay.kind === 'acked') {
      expect(replay.acknowledgedRevisions[items[0].id]).toBe(revisions[0])
      expect(replay.acknowledgedRevisions[items[0].id]).not.toBe(snapshotRevision(grown[0]))
    }
  })

  it('mints a new publish messageId when recovery creates a new Activation', async () => {
    let sentMessageId = ''
    vi.stubGlobal('fetch', protocolFetch(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const request = JSON.parse(String(init?.body)) as { messageId: string }
      sentMessageId = request.messageId
      return protocolResponse(
        'facts.ack',
        { results: [{ index: 0, status: 'duplicate' }] },
        ACTIVATION_ID,
        request.messageId,
      )
    }))
    const currentSession: BrowserProtocolSession = {
      port: 24820,
      activationId: ACTIVATION_ID,
      leaseToken: 'lease',
      streamId: STREAM_ID,
      specRevision: 3,
      expiresAt: '2026-08-25T08:01:00Z',
      limits: { maxFactsPerBatch: 1, maxBatchBytes: 1_048_576 },
      flushPeriodMilliseconds: 30_000,
    }
    const staleMessageId = '0198d5e8-30cc-743c-a3d6-ac61956f26b6'
    const staleAttempt = {
      activationId: '0198d5e8-30cb-7d54-bab1-250087147e4d',
      messageId: staleMessageId,
      snapshots: [snapshot()],
    }

    const result = await publishBrowserFacts(currentSession, [snapshot()], staleAttempt)

    expect(result.kind).toBe('acked')
    expect(sentMessageId).not.toBe(staleMessageId)
  })

  it('uses a conservative UTF-8 limit and skips an oversized head without dropping it', async () => {
    let publishedFactId = ''
    vi.stubGlobal('fetch', protocolFetch(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const request = JSON.parse(String(init?.body)) as {
        body: { facts: { factId: string }[] }
      }
      publishedFactId = request.body.facts[0].factId
      const wire = JSON.parse(String(init?.body)) as { messageId: string }
      return protocolResponse(
        'facts.ack',
        { results: [{ index: 0, status: 'committed' }] },
        session.activationId,
        wire.messageId,
      )
    }))
    const session: BrowserProtocolSession = {
      port: 24820,
      activationId: '0198d5e8-30cb-7d54-bab1-250087147e4c',
      leaseToken: 'lease',
      streamId: '0198d5e2-e0d4-7b30-9da7-342ee261bf62',
      specRevision: 3,
      expiresAt: '2026-08-25T08:01:00Z',
      limits: { maxFactsPerBatch: 2, maxBatchBytes: 900 },
      flushPeriodMilliseconds: 30_000,
    }
    const oversized = { ...snapshot(), title: '你'.repeat(500) }
    const deliverable = snapshot('0198d5eb-fc31-7d7b-8bf0-c2d009ec8998')

    const result = await publishBrowserFacts(session, [oversized, deliverable])

    expect(result.kind).toBe('acked')
    expect(publishedFactId).toBe(deliverable.id)
    if (result.kind === 'acked') expect(result.acknowledgedIds).toEqual([deliverable.id])
  })

  it('surfaces permanent rejection and retry timing separately from ACKs', async () => {
    let sentMessageId = ''
    vi.stubGlobal('fetch', protocolFetch(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const wire = JSON.parse(String(init?.body)) as { messageId: string }
      sentMessageId = wire.messageId
      return protocolResponse('facts.ack', {
        results: [
          { index: 0, status: 'rejected', error: { code: 'fact_schema_invalid' } },
          { index: 1, status: 'retry', retryAfterMs: 4_000, error: { code: 'hub_backpressure' } },
        ],
      }, ACTIVATION_ID, wire.messageId)
    }))
    const session: BrowserProtocolSession = {
      port: 24820,
      activationId: '0198d5e8-30cb-7d54-bab1-250087147e4c',
      leaseToken: 'lease',
      streamId: '0198d5e2-e0d4-7b30-9da7-342ee261bf62',
      specRevision: 3,
      expiresAt: '2026-08-25T08:01:00Z',
      limits: { maxFactsPerBatch: 2, maxBatchBytes: 1_048_576 },
      flushPeriodMilliseconds: 30_000,
    }
    const rejected = snapshot()
    const retry = snapshot('0198d5eb-fc31-7d7b-8bf0-c2d009ec8998')

    const result = await publishBrowserFacts(session, [rejected, retry])

    expect(result.kind).toBe('acked')
    if (result.kind === 'acked') {
      expect(result.acknowledgedIds).toEqual([])
      expect(result.rejectedRevisions[rejected.id]).toBe(snapshotRevision(rejected))
      expect(result.retryAfterMilliseconds).toBe(4_000)
      expect(result.nextPublishAttempt?.snapshots).toEqual([retry])
      expect(result.nextPublishAttempt?.messageId).not.toBe(sentMessageId)
    }
  })

  it('does not treat a miscorrelated 2xx response as an explicit Fact ACK', async () => {
    vi.stubGlobal('fetch', protocolFetch(async () => protocolResponse(
      'facts.ack',
      { results: [{ index: 0, status: 'committed' }] },
      ACTIVATION_ID,
      '0198d5e8-30cc-743c-a3d6-ac61956f26b6',
    )))
    const session: BrowserProtocolSession = {
      port: 24820,
      activationId: ACTIVATION_ID,
      leaseToken: 'lease',
      streamId: STREAM_ID,
      specRevision: 3,
      expiresAt: '2026-08-25T08:01:00Z',
      limits: { maxFactsPerBatch: 1, maxBatchBytes: 1_048_576 },
      flushPeriodMilliseconds: 30_000,
    }

    const result = await publishBrowserFacts(session, [snapshot()])

    expect(result.kind).toBe('unavailable')
    if (result.kind === 'unavailable') expect(result.publishAttempt?.snapshots).toEqual([snapshot()])
  })

  it('reports bounded-outbox loss through the durable stream-gap capability', async () => {
    let request: { messageId?: string; body?: { gap?: { gapId?: string; reason?: string } } } = {}
    vi.stubGlobal('fetch', protocolFetch(async (_input: RequestInfo | URL, init?: RequestInit) => {
      request = JSON.parse(String(init?.body))
      return protocolResponse(
        'stream.gapAck',
        { streamId: STREAM_ID },
        ACTIVATION_ID,
        request.messageId,
      )
    }))
    const session: BrowserProtocolSession = {
      port: 24820,
      activationId: '0198d5e8-30cb-7d54-bab1-250087147e4c',
      leaseToken: 'lease',
      streamId: '0198d5e2-e0d4-7b30-9da7-342ee261bf62',
      specRevision: 3,
      expiresAt: '2026-08-25T08:01:00Z',
      limits: { maxFactsPerBatch: 2, maxBatchBytes: 1_048_576 },
      flushPeriodMilliseconds: 30_000,
    }
    const gap: BrowserPendingGap = {
      gapId: '0198d5eb-fc30-7d7b-8bf0-c2d009ec8999',
      messageId: '0198d5eb-fc31-7d7b-8bf0-c2d009ec8999',
      activationId: ACTIVATION_ID,
      start: '2026-08-25T08:00:00.000Z',
      end: '2026-08-25T08:01:00.000Z',
      reason: 'buffer_overflow',
      estimatedFactsLost: 3,
    }

    await expect(reportBrowserGap(session, gap)).resolves.toBe('acked')
    expect(request.messageId).toBe(gap.messageId)
    expect(request.body?.gap?.gapId).toMatch(/^[0-9a-f-]{36}$/)
    expect(request.body?.gap?.reason).toBe('buffer_overflow')
  })
})
