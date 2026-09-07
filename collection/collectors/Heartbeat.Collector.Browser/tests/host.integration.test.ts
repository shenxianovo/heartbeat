import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve } from 'node:path'
import { afterAll, beforeAll, expect, it, vi } from 'vitest'
import { probeHub } from '../src/hub'
import { publishBrowserFacts, reportBrowserGap, uploadWithBrowserProtocol, type BrowserProtocolSession } from '../src/protocol'
import { uuidv7 } from '../src/ids'

const root = resolve(import.meta.dirname, '../../../..')
const project = resolve(root, 'collection/collectors/Heartbeat.Collector.Browser.TestHost')
const nativeFetch = globalThis.fetch
let child: ChildProcess | undefined
let temp: string
let port: number
let reference: Record<string, string>
let exited: Promise<void>
let firstSession: BrowserProtocolSession

beforeAll(async () => {
  temp = mkdtempSync(resolve(tmpdir(), 'heartbeat-browser-http-'))
  execFileSync('node', [resolve(root, 'scripts/collector-contracts.mjs'), 'stage', 'browser', resolve(temp, 'package'), '--include-current-test-platform'])
  reference = JSON.parse(readFileSync(resolve(temp, 'package/browser-extension/collector-artifact-ref.json'), 'utf8'))
  execFileSync('dotnet', ['build', project, '-c', 'Release', '--nologo'], { timeout: 120_000 })
  child = spawn('dotnet', [resolve(project, 'bin/Release/net10.0/Heartbeat.Collector.Browser.TestHost.dll'), resolve(temp, 'package'), resolve(temp, 'data')], { stdio: ['pipe', 'pipe', 'pipe'] })
  exited = new Promise(resolve => child!.once('exit', () => resolve()))
  port = await new Promise<number>((resolve, reject) => {
    let output = ''
    const timer = setTimeout(() => reject(new Error(`Host startup timed out: ${output}`)), 15_000)
    child!.on('error', reject)
    child!.on('exit', code => { clearTimeout(timer); reject(new Error(`Host exited ${code}: ${output}`)) })
    child!.stderr!.on('data', bytes => { output += bytes.toString() })
    child!.stdout!.on('data', bytes => {
      output += bytes.toString()
      const match = output.match(/http:\/\/127\.0\.0\.1:(\d+)/)
      if (match) { clearTimeout(timer); resolve(Number(match[1])) }
    })
  })
  vi.stubGlobal('chrome', { runtime: { getURL: (path: string) => `chrome-extension://fixture/${path}` } })
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) =>
    String(input).startsWith('chrome-extension://')
      ? Promise.resolve(Response.json(reference)) : nativeFetch(input, init))
}, 150_000)

afterAll(async () => {
  vi.unstubAllGlobals()
  if (child) {
    child.stdin?.end('\n')
    const timer = setTimeout(() => child?.kill('SIGKILL'), 5_000)
    await exited
    clearTimeout(timer)
  }
  if (temp) rmSync(temp, { recursive: true, force: true })
})

async function connect(identity: string, app: string, previous?: BrowserProtocolSession) {
  const start = new Date(Date.now() - 60_000).toISOString()
  return uploadWithBrowserProtocol(port, app, identity, [{
    id: uuidv7(), source: 'browser', identityKey: 'https://example.com/docs', title: 'Docs',
    startTime: start, endTime: new Date().toISOString(), isFinal: true,
    attributes: { url: 'https://example.com/docs', domain: 'example.com', site: 'example.com', windowId: 1 },
  }], previous)
}

it('connects the actual Browser client to the generic Host and projects its App/URL identities', async () => {
  expect(await probeHub(port)).toBe(true)
  const result = await connect('profile-a', 'win:chrome')
  expect(result.kind).toBe('acked')
  if (result.kind !== 'acked') throw new Error(JSON.stringify(result))
  firstSession = result.session
  expect(result.acknowledgedIds).toHaveLength(1)
  const state = await (await nativeFetch(`http://127.0.0.1:${port}/test/status`)).json()
  expect(state.instances).toBe(1)
  expect(state.status.connectedExternalHosts).toBe(1)
  expect(state.facts).toMatchObject([{ source: 'browser', appIdentityKey: 'win:chrome', identityKey: 'https://example.com/docs' }])
})

it('isolates Profiles, reconnects only the same identity, and preserves Streams across Host restart', async () => {
  const second = await connect('profile-b', 'win:msedge')
  expect(second.kind).toBe('acked')
  if (second.kind !== 'acked') throw new Error(JSON.stringify(second))
  const reconnect = await connect('profile-a', 'win:chrome')
  expect(reconnect.kind).toBe('acked')
  if (reconnect.kind !== 'acked') throw new Error(JSON.stringify(reconnect))
  expect(reconnect.session.activationId).not.toBe(firstSession.activationId)
  expect(reconnect.session.streamId).toBe(firstSession.streamId)
  expect(second.session.streamId).not.toBe(firstSession.streamId)
  const otherStillRunning = await connect('profile-b', 'win:msedge', second.session)
  expect(otherStillRunning.kind).toBe('acked')
  if (otherStillRunning.kind === 'acked') expect(otherStillRunning.session.activationId).toBe(second.session.activationId)
  const state = await (await nativeFetch(`http://127.0.0.1:${port}/test/status`)).json()
  expect(state.instances).toBe(1)
  expect(state.status.connectedExternalHosts).toBe(2)

  await nativeFetch(`http://127.0.0.1:${port}/test/restart`, { method: 'POST' })
  const restored = await connect('profile-a', 'win:chrome')
  expect(restored.kind).toBe('acked')
  if (restored.kind !== 'acked') throw new Error(JSON.stringify(restored))
  expect(restored.session.streamId).toBe(firstSession.streamId)
  firstSession = restored.session
  expect((await connect('profile-a', 'win:msedge')).kind).toBe('unavailable')
  expect((await connect('profile-a', 'win:chrome', firstSession)).kind).toBe('acked')
})

it('rejects an exact Package mismatch without disturbing a healthy Activation', async () => {
  const correct = reference
  reference = { ...reference, packageContentHash: `sha256:${'0'.repeat(64)}` }
  expect((await connect('profile-c', 'win:chrome')).kind).toBe('unavailable')
  reference = correct
  const result = await connect('profile-a', 'win:chrome', firstSession)
  expect(result.kind).toBe('acked')
  if (result.kind === 'acked') expect(result.session.activationId).toBe(firstSession.activationId)
})

it('retries a lost Fact ACK idempotently and commits a durable Stream Gap through the real binding', async () => {
  const item = {
    id: uuidv7(), source: 'browser' as const, identityKey: 'https://example.com/retry', title: 'Retry',
    startTime: new Date(Date.now() - 60_000).toISOString(), endTime: new Date().toISOString(), isFinal: true,
    attributes: { url: 'https://example.com/retry', domain: 'example.com', site: 'example.com', windowId: 1 },
  }
  vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
    await nativeFetch(input, init)
    throw new Error('Response lost after commit')
  })
  const lost = await publishBrowserFacts(firstSession, [item])
  expect(lost.kind).toBe('unavailable')
  vi.stubGlobal('fetch', nativeFetch)
  if (lost.kind !== 'unavailable') throw new Error(JSON.stringify(lost))
  const retry = await publishBrowserFacts(firstSession, [item], lost.publishAttempt)
  expect(retry.kind).toBe('acked')
  if (retry.kind === 'acked') expect(retry.acknowledgedIds).toEqual([item.id])
  const gap = { gapId: uuidv7(), start: item.startTime, end: item.endTime, reason: 'buffer_overflow' as const, estimatedFactsLost: 1 }
  expect(await reportBrowserGap(firstSession, gap)).toBe('acked')
  expect(await reportBrowserGap(firstSession, gap)).toBe('acked')
})

it('revokes all leases on removal and rejects reconnect without recreating the Instance', async () => {
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) =>
    String(input).startsWith('chrome-extension://')
      ? Promise.resolve(Response.json(reference)) : nativeFetch(input, init))
  expect((await nativeFetch(`http://127.0.0.1:${port}/test/remove`, { method: 'POST' })).ok).toBe(true)
  expect((await connect('profile-a', 'win:chrome', firstSession)).kind).toBe('unavailable')
  const response = await nativeFetch(`http://127.0.0.1:${port}/v1/collector-protocol/external-host/hello`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ protocol: 'heartbeat.collector.bootstrap/1', type: 'activation.hello', messageId: uuidv7(), body: {
      ...reference, externalHostIdentity: 'profile-a', appIdentityKey: 'win:chrome',
      protocolMajors: [1], supportedCapabilities: { 'facts.segment': [1], 'diagnostics.stream-gap': [1] },
    } }),
  })
  expect(await response.json()).toMatchObject({ body: { error: { code: 'package_not_installed' } } })
  const state = await (await nativeFetch(`http://127.0.0.1:${port}/test/status`)).json()
  expect(state.instances).toBe(0)
  expect(await probeHub(port)).toBe(true)
})
