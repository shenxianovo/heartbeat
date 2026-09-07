import { execFileSync, spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { chmodSync, mkdtempSync, readFileSync, readdirSync, rmSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve } from 'node:path'
import { afterEach, expect, it } from 'vitest'

const root = resolve(import.meta.dirname, '../../../..')
const python = process.platform === 'win32' ? 'python' : 'python3'
const temps: string[] = []
afterEach(() => { for (const path of temps.splice(0)) rmSync(path, { recursive: true, force: true }) })

function fixture(version = '1.2.3') {
  const temp = mkdtempSync(resolve(tmpdir(), 'heartbeat-browser-release-'))
  temps.push(temp)
  execFileSync('node', [resolve(root, 'scripts/collector-contracts.mjs'), 'stage', 'browser', resolve(temp, 'package'), '--version', version])
  return temp
}
function release(temp: string, name = 'release', version = '1.2.3') {
  return spawnSync(python, [resolve(root, 'scripts/package-browser-release.py'), '--package', resolve(temp, 'package'), '--version', version, '--output', resolve(temp, name)], { encoding: 'utf8' })
}

it('publishes identical deterministic artifact bytes for all four desktop targets', () => {
  const temp = fixture()
  const first = release(temp)
  expect(first.status, first.stderr).toBe(0)
  const payload = resolve(temp, 'package/browser-extension/background.js')
  utimesSync(payload, new Date(), new Date())
  chmodSync(payload, 0o700)
  const second = release(temp, 'second')
  expect(second.status, second.stderr).toBe(0)
  const name = 'heartbeat.collector.browser-1.2.3.zip'
  const bytes = readFileSync(resolve(temp, 'release', name))
  expect(readFileSync(resolve(temp, 'second', name))).toEqual(bytes)
  const catalog = JSON.parse(readFileSync(resolve(temp, 'release/catalog-entry.json'), 'utf8'))
  expect(catalog.latest.map((item: { target: object }) => item.target)).toEqual([
    { os: 'windows', arch: 'x64' }, { os: 'windows', arch: 'arm64' },
    { os: 'macos', arch: 'x64' }, { os: 'macos', arch: 'arm64' },
  ])
  for (const item of catalog.latest) {
    const target = `${item.target.os}-${item.target.arch}`
    const metadata = JSON.parse(readFileSync(resolve(temp, 'release', target, 'release.json'), 'utf8'))
    expect(metadata.artifact).toMatchObject({ fileName: name, length: bytes.length, sha256: `sha256:${createHash('sha256').update(bytes).digest('hex')}` })
    expect(item.releaseUrl).toBe(`https://heartbeat.shenxianovo.com/collector-registry/v1/packages/heartbeat.collector.browser/versions/1.2.3/${target}/release.json`)
  }
  expect(readdirSync(resolve(temp, 'release')).filter(name => name.endsWith('.zip'))).toEqual([name])
})

it('rejects a tag/Package version mismatch before producing a release', () => {
  const temp = fixture()
  expect(release(temp, 'release', '1.2.4').status).not.toBe(0)
  expect(readdirSync(temp)).toEqual(['package'])
})

function publish(temp: string, registry: string, phase: string) {
  return spawnSync(python, [resolve(root, 'scripts/publish-collector-release.py'), '--release', resolve(temp, 'release'), '--registry', registry, '--phase', phase], { encoding: 'utf8' })
}

it.skipIf(process.platform === 'win32')('installs immutable releases, survives reruns, and never rolls Catalog Latest backwards', () => {
  const temp = fixture()
  expect(release(temp).status).toBe(0)
  const registry = resolve(temp, 'registry')
  let result = publish(temp, registry, 'install')
  expect(result.status, result.stderr).toBe(0)
  result = publish(temp, registry, 'catalog')
  expect(result.status, result.stderr).toBe(0)
  const catalogPath = resolve(registry, 'catalog.json')
  const catalog = JSON.parse(readFileSync(catalogPath, 'utf8'))
  catalog.packages.push({ packageId: 'unrelated.collector', displayName: 'Other', summary: 'Preserve me', latest: [] })
  writeFileSync(catalogPath, JSON.stringify(catalog))
  expect(publish(temp, registry, 'install').status).toBe(0)
  expect(publish(temp, registry, 'catalog').status).toBe(0)
  expect(JSON.parse(readFileSync(catalogPath, 'utf8'))).toEqual(catalog)

  const older = fixture('1.2.2')
  expect(release(older, 'release', '1.2.2').status).toBe(0)
  expect(publish(older, registry, 'install').status).toBe(0)
  expect(publish(older, registry, 'catalog').status).toBe(0)
  expect(JSON.parse(readFileSync(catalogPath, 'utf8'))).toEqual(catalog)

  const target = resolve(registry, 'packages/heartbeat.collector.browser/versions/1.2.3/windows-x64/heartbeat.collector.browser-1.2.3.zip')
  writeFileSync(target, 'different immutable bytes')
  expect(publish(temp, registry, 'install').status).not.toBe(0)
  expect(readFileSync(target, 'utf8')).toBe('different immutable bytes')
  expect(publish(temp, registry, 'catalog').status).not.toBe(0)
})
