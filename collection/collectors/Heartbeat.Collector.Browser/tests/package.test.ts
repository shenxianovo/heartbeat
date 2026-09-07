import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve } from 'node:path'
import { expect, it } from 'vitest'

const root = resolve(import.meta.dirname, '../../../..')
it('stages one Marketplace Package with an exact bootstrap reference for every desktop target', () => {
  const temp = mkdtempSync(resolve(tmpdir(), 'heartbeat-browser-package-'))
  try {
    execFileSync('node', [resolve(root, 'scripts/collector-contracts.mjs'), 'stage', 'browser', resolve(temp, 'package'), '--version', '1.2.3'])
    const bytes = readFileSync(resolve(temp, 'package/collector-manifest.json'))
    const manifest = JSON.parse(bytes.toString())
    const reference = JSON.parse(readFileSync(resolve(temp, 'package/browser-extension/collector-artifact-ref.json'), 'utf8'))
    expect(manifest.version).toBe('1.2.3')
    expect(JSON.parse(readFileSync(resolve(temp, 'package/browser-extension/manifest.json'), 'utf8')).version).toBe('1.2.3')
    expect(manifest.defaultInstance).toEqual({ subjectKind: 'machine', configVersion: 1, config: { enabled: true, flushPeriodMs: 30000 } })
    expect(manifest.presentation.displayName).toBe('Browser')
    expect(manifest.outputs[0].dimensionKeys).toEqual(['appIdentityKey', 'externalHostIdentity'])
    expect(reference).toEqual({
      packageId: manifest.packageId, packageVersion: manifest.version,
      packageContentHash: `sha256:${createHash('sha256').update(bytes).digest('hex')}`,
      artifactId: 'browser.extension', artifactHash: manifest.artifacts[0].contentHash,
    })
  } finally { rmSync(temp, { recursive: true, force: true }) }
})
