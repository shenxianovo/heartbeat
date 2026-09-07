import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { publishBrowserFacts, type BrowserProtocolSession } from '../src/protocol'
import type { SegmentSnapshot } from '../src/fold'

type FactCase = {
  status: 'committed' | 'duplicate' | 'superseded' | 'rejected' | 'retry'
  removesFact: boolean
  startsNewAttempt: boolean
}

type DrainCase = {
  reason: string
  remainderDurable: boolean
  canBeFullyDrained: boolean
}

const corpusPath = fileURLToPath(new URL(
  '../../../protocol/conformance/v1/collector-protocol-conformance.json',
  import.meta.url,
))
const corpus = JSON.parse(readFileSync(corpusPath, 'utf8')) as {
  schemaVersion: number
  lifecycle: string[]
  factAcknowledgements: FactCase[]
  drainOutcomes: DrainCase[]
  completionOutcomes: string[]
  drainDrivers: Array<{ driver: string, hubInitiated: boolean, deadlineAction: string }>
}

const snapshot: SegmentSnapshot = {
  id: '0198d5eb-fc31-7d7b-8bf0-c2d009ec8999',
  source: 'browser',
  identityKey: 'https://example.com/docs',
  title: 'Docs',
  startTime: '2026-08-25T08:00:00.000Z',
  endTime: '2026-08-25T08:01:00.000Z',
  isFinal: false,
  attributes: { url: 'https://example.com/docs', domain: 'example.com', site: 'example.com', windowId: 7 },
}

const session: BrowserProtocolSession = {
  port: 24820,
  activationId: '0198d5e8-30cb-7d54-bab1-250087147e4c',
  leaseToken: 'lease',
  streamId: '0198d5e2-e0d4-7b30-9da7-342ee261bf62',
  specRevision: 1,
  expiresAt: '2026-08-25T08:10:00Z',
  limits: { maxFactsPerBatch: 500, maxBatchBytes: 1_048_576 },
  flushPeriodMilliseconds: 30_000,
}

afterEach(() => vi.unstubAllGlobals())

describe('Collector Protocol conformance corpus', () => {
  it('keeps the canonical lifecycle shared with non-TypeScript adapters', () => {
    expect(corpus.schemaVersion).toBe(1)
    expect(corpus.lifecycle).toEqual([
      'activation.hello',
      'activation.initialize',
      'activation.initialized',
      'streams.open',
      'streams.opened',
      'activation.ready',
      'activation.readyAck',
      'activation.drain',
      'activation.drained',
    ])
  })

  it('shares bounded drain outcomes across all three drivers', () => {
    expect(corpus.drainOutcomes).toEqual([
      { reason: 'drained', remainderDurable: true, canBeFullyDrained: true },
      { reason: 'deadline_exceeded', remainderDurable: false, canBeFullyDrained: false },
      { reason: 'stop_failed', remainderDurable: false, canBeFullyDrained: false },
      { reason: 'flush_cancelled', remainderDurable: true, canBeFullyDrained: false },
      { reason: 'persistence_failed', remainderDurable: false, canBeFullyDrained: false },
    ])
    expect(corpus.completionOutcomes).toEqual([
      'completed', 'deadline_exceeded', 'completion_failed',
    ])
    expect(corpus.drainDrivers).toEqual([
      { driver: 'in_process', hubInitiated: true, deadlineAction: 'fence_and_release' },
      { driver: 'managed_process', hubInitiated: true, deadlineAction: 'terminate_and_release' },
      { driver: 'external_host', hubInitiated: false, deadlineAction: 'revoke_lease' },
    ])
  })

  for (const item of corpus.factAcknowledgements) {
    it(`applies canonical ${item.status} delivery semantics`, async () => {
      vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        const request = JSON.parse(String(init?.body)) as { messageId: string }
        return Response.json({
          protocol: 'heartbeat.collector/1',
          type: 'facts.ack',
          messageId: '0198d5e8-30cc-743c-a3d6-ac61956f26b5',
          activationId: session.activationId,
          replyTo: request.messageId,
          body: {
            results: [{
              index: 0,
              status: item.status,
              ...(item.status === 'retry' ? {
                retryAfterMs: 1,
                error: { code: 'fixture_retry', message: 'Retry.', retryable: true },
              } : {}),
              ...(item.status === 'rejected' ? {
                error: { code: 'fixture_rejected', message: 'Rejected.', retryable: false },
              } : {}),
            }],
          },
        })
      }))

      const result = await publishBrowserFacts(session, [snapshot])

      expect(result.kind).toBe('acked')
      if (result.kind !== 'acked') throw new Error('Expected an ACK result')
      const removed = result.acknowledgedIds.includes(snapshot.id) ||
        Object.hasOwn(result.rejectedRevisions, snapshot.id)
      expect(removed).toBe(item.removesFact)
      expect(result.nextPublishAttempt !== undefined).toBe(item.startsNewAttempt)
    })
  }
})
