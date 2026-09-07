#!/usr/bin/env node
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import {
  cpSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const factsDirectory = join(root, 'collection/contracts/facts')
const baselineFileName = 'fact-schema-evolution-baseline.json'
const baselinePath = join(factsDirectory, baselineFileName)

const packageSources = {
  browser: join(root, 'collection/collectors/Heartbeat.Collector.Browser/Package'),
  system: join(root, 'collection/desktop/Heartbeat.Collector.System/Package'),
  'reference-fixture': join(root, 'collection/hub/Heartbeat.Collection.Hub.Tests/Fixtures/ReferenceCollectorPackage'),
}

function sha256(content) {
  return `sha256:${createHash('sha256').update(content).digest('hex')}`
}

function normalizeJson(value) {
  if (Array.isArray(value)) return value.map(normalizeJson)
  if (value !== null && typeof value === 'object')
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, normalizeJson(value[key])]))
  return value
}

function semanticJsonHash(value) {
  return sha256(Buffer.from(JSON.stringify(normalizeJson(value))))
}

function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8'))
}

function factContracts() {
  return readdirSync(factsDirectory)
    .filter(name => name.endsWith('.schema.json'))
    .sort()
    .map(name => {
      const path = join(factsDirectory, name)
      const bytes = readFileSync(path)
      const document = JSON.parse(bytes)
      return {
        name,
        path,
        bytes,
        document,
        contentHash: sha256(bytes),
        evolutionHash: semanticJsonHash(document),
      }
    })
}

function validateContracts(contracts) {
  const identities = new Set()
  for (const contract of contracts) {
    const value = contract.document
    for (const field of ['schemaId', 'schemaMajor', 'schemaRevision', 'factKind', 'payloadSchema']) {
      if (value[field] === undefined) throw new Error(`${contract.name}: missing ${field}`)
    }
    if (!['segment', 'event'].includes(value.factKind))
      throw new Error(`${contract.name}: executable Collector Protocol v1 supports only segment/event`)
    const identity = `${value.schemaId}@${value.schemaMajor}.${value.schemaRevision}`
    if (identities.has(identity)) throw new Error(`duplicate Fact Schema identity ${identity}`)
    identities.add(identity)
  }
  if (contracts.length !== 5) throw new Error(`expected exactly 5 authoritative Fact Schemas, found ${contracts.length}`)
}

function baselineFor(contracts) {
  return {
    formatVersion: 2,
    contracts: contracts.map(contract => ({
      schemaId: contract.document.schemaId,
      schemaMajor: contract.document.schemaMajor,
      schemaRevision: contract.document.schemaRevision,
      hash: contract.evolutionHash,
      document: contract.name,
    })),
  }
}

function compareBaseline(expected, actual, label) {
  const expectedText = `${JSON.stringify(expected, null, 2)}\n`
  const actualText = `${JSON.stringify(actual, null, 2)}\n`
  if (expectedText !== actualText)
    throw new Error(`${label} is stale; run: node scripts/collector-contracts.mjs baseline`)
}

function checkBaseRef(current, baseRef) {
  let oldText
  try {
    try {
      oldText = execFileSync(
        'git',
        ['show', `${baseRef}:collection/contracts/facts/${baselineFileName}`],
        { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
      )
    } catch {
      // Compatibility for review bases created before the baseline filename migration. Remove this
      // fallback once every supported CI base contains fact-schema-evolution-baseline.json;
      // `check --base-ref <base>` is the verification gate.
      oldText = execFileSync(
        'git',
        ['show', `${baseRef}:collection/contracts/facts/baseline.json`],
        { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
      )
    }
  } catch {
    process.stdout.write(`Contract baseline does not exist at ${baseRef}; treating this branch as the initial baseline.\n`)
    return
  }
  const old = JSON.parse(oldText)
  if (![1, 2].includes(old.formatVersion))
    throw new Error(`unsupported Fact Schema baseline format ${old.formatVersion} at ${baseRef}`)
  const currentByIdentity = new Map(current.contracts.map(item => [
    `${item.schemaId}@${item.schemaMajor}.${item.schemaRevision}`,
    item,
  ]))
  for (const previous of old.contracts) {
    const identity = `${previous.schemaId}@${previous.schemaMajor}.${previous.schemaRevision}`
    const candidate = currentByIdentity.get(identity)
    const previousHash = old.formatVersion === 2
      ? previous.hash
      : semanticJsonHash(JSON.parse(execFileSync(
        'git',
        ['show', `${baseRef}:collection/contracts/facts/${previous.document}`],
        { cwd: root, encoding: 'utf8' },
      )))
    if (candidate && candidate.hash !== previousHash)
      throw new Error(`${identity} changed meaning without changing schemaMajor/schemaRevision`)
  }
}

function checkBrowserPayload() {
  const dist = join(root, 'collection/collectors/Heartbeat.Collector.Browser/dist')
  const packaged = join(root, 'collection/collectors/Heartbeat.Collector.Browser/Package/browser-extension')
  if (!existsSync(dist))
    throw new Error('Browser dist is missing; run npm run build before contract check')
  const snapshot = directory => Object.fromEntries(listFiles(directory)
    .filter(path => !path.endsWith('collector-artifact-ref.json'))
    .map(path => [relative(directory, path).replaceAll('\\', '/'), sha256(readFileSync(path))])
    .sort(([left], [right]) => left.localeCompare(right)))
  if (JSON.stringify(snapshot(dist)) !== JSON.stringify(snapshot(packaged)))
    throw new Error('Browser source and packaged extension differ; run npm run build and sync dist into Package/browser-extension')
}

function copyPackageSource(source, destination) {
  cpSync(source, destination, {
    recursive: true,
    filter: path => {
      const name = relative(source, path).replaceAll('\\', '/')
      return name !== 'collector-manifest.json' &&
        name !== 'collector-manifest.template.json' &&
        name !== 'schemas' && !name.startsWith('schemas/')
    },
  })
}

function listFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name)
    return entry.isDirectory() ? listFiles(path) : [path]
  })
}

function stageBrowserArtifact(destination) {
  const extension = join(destination, 'browser-extension')
  if (!existsSync(join(extension, 'background.js')))
    throw new Error('Browser Package payload is missing; run npm run build and sync dist first')
  rmSync(join(extension, 'collector-artifact-ref.json'), { force: true })
  const files = listFiles(extension)
    .filter(path => !path.endsWith('collector-artifact-ref.json'))
    .sort()
    .map(path => {
      const bytes = readFileSync(path)
      return {
        path: relative(destination, path).replaceAll('\\', '/'),
        size: bytes.length,
        contentHash: sha256(bytes),
      }
    })
  const descriptor = Buffer.from(`${JSON.stringify({
    kind: 'heartbeat.browser.external-host',
    entrypoint: 'browser-extension/manifest.json',
    files,
  }, null, 2)}\n`)
  const descriptorPath = join(destination, 'artifacts/browser-extension.artifact.json')
  mkdirSync(dirname(descriptorPath), { recursive: true })
  writeFileSync(descriptorPath, descriptor)

}

function populateContentReferences(destination, manifest) {
  if (manifest.observationDeclaration) {
    const declaration = readFileSync(join(destination, manifest.observationDeclaration.document))
    manifest.observationDeclaration.hash = sha256(declaration)
  }
  for (const artifact of manifest.artifacts) {
    const content = readFileSync(join(destination, artifact.entrypoint))
    artifact.size = content.length
    artifact.contentHash = sha256(content)
  }
}

function validateGeneratedReferencesAreNotPinned() {
  for (const [name, source] of Object.entries(packageSources)) {
    const manifest = readJson(join(source, 'collector-manifest.template.json'))
    if (manifest.observationDeclaration?.hash !== undefined)
      throw new Error(`${name}: observation declaration hash must be generated during staging`)
    for (const artifact of manifest.artifacts) {
      if (artifact.size !== undefined || artifact.contentHash !== undefined)
        throw new Error(`${name}: Artifact size/hash must be generated during staging`)
    }
  }
}

function currentPlatform() {
  const operatingSystem = {
    win32: 'windows',
    darwin: 'macos',
    linux: 'linux',
  }[process.platform]
  const architecture = {
    x64: 'x64',
    arm64: 'arm64',
  }[process.arch]
  if (!operatingSystem || !architecture)
    throw new Error(`unsupported staging platform ${process.platform}/${process.arch}`)
  return { operatingSystem, architecture }
}

function includeCurrentTestPlatform(manifest) {
  const { operatingSystem, architecture } = currentPlatform()
  for (const artifact of manifest.artifacts) {
    if (!artifact.selector.os.includes(operatingSystem)) artifact.selector.os.push(operatingSystem)
    if (!artifact.selector.arch.includes(architecture)) artifact.selector.arch.push(architecture)
  }
}

function stagePackage(name, destination, includeTestPlatform = false, version) {
  const source = packageSources[name]
  if (!source) throw new Error(`unknown package '${name}'`)
  const output = resolve(destination)
  rmSync(output, { recursive: true, force: true })
  mkdirSync(output, { recursive: true })
  copyPackageSource(source, output)
  const manifest = readJson(join(source, 'collector-manifest.template.json'))
  if (includeTestPlatform) includeCurrentTestPlatform(manifest)
  if (version !== undefined) manifest.version = version
  if (name === 'browser') {
    const extensionManifestPath = join(output, 'browser-extension/manifest.json')
    const extensionManifest = readJson(extensionManifestPath)
    extensionManifest.version = manifest.version
    writeFileSync(extensionManifestPath, `${JSON.stringify(extensionManifest, null, 2)}\n`)
    stageBrowserArtifact(output)
  }
  populateContentReferences(output, manifest)
  const contracts = factContracts()
  const byId = new Map(contracts.map(contract => [contract.document.schemaId, contract]))
  for (const outputDeclaration of manifest.outputs) {
    const contract = byId.get(outputDeclaration.schema.id)
    if (!contract) throw new Error(`${name}: unknown schema ${outputDeclaration.schema.id}`)
    const schema = outputDeclaration.schema
    if (schema.major !== contract.document.schemaMajor || schema.revision !== contract.document.schemaRevision)
      throw new Error(`${name}: manifest identity does not match ${contract.name}`)
    const expectedDocument = `schemas/${contract.name}`
    if (schema.document !== expectedDocument)
      throw new Error(`${name}: schema ${schema.id} must retain authoritative basename ${expectedDocument}`)
    const target = join(output, schema.document)
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, contract.bytes)
    schema.hash = contract.contentHash
  }
  const manifestBytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`)
  writeFileSync(join(output, 'collector-manifest.json'), manifestBytes)
  if (name === 'browser') {
    // Bootstrap reference is derived from the final manifest, outside its descriptor's payload
    // hash to avoid a circular identity. It grants no authority: the Host compares every field.
    writeFileSync(join(output, 'browser-extension/collector-artifact-ref.json'), `${JSON.stringify({
      packageId: manifest.packageId,
      packageVersion: manifest.version,
      packageContentHash: sha256(manifestBytes),
      artifactId: manifest.artifacts[0].artifactId,
      artifactHash: manifest.artifacts[0].contentHash,
    }, null, 2)}\n`)
  }
  process.stdout.write(`Staged ${name} Collector Package at ${output}\n`)
}

const [command, ...args] = process.argv.slice(2)
try {
  const contracts = factContracts()
  validateContracts(contracts)
  const baseline = baselineFor(contracts)
  if (command === 'baseline') {
    writeFileSync(baselinePath, `${JSON.stringify(baseline, null, 2)}\n`)
  } else if (command === 'check') {
    validateGeneratedReferencesAreNotPinned()
    compareBaseline(baseline, readJson(baselinePath), 'Fact Schema baseline')
    checkBrowserPayload()
    const baseIndex = args.indexOf('--base-ref')
    if (baseIndex >= 0) checkBaseRef(baseline, args[baseIndex + 1])
    process.stdout.write('Collector Fact Schemas and evolution baseline are consistent.\n')
  } else if (command === 'stage' && args.length >= 2) {
    let includeTestPlatform = false
    let version
    for (let index = 2; index < args.length; index++) {
      if (args[index] === '--include-current-test-platform') includeTestPlatform = true
      else if (args[index] === '--version' && args[0] === 'browser' && version === undefined) {
        version = args[++index]
        if (!/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(version ?? '') ||
            version.split('.').some(part => Number(part) > 65535) || version === '0.0.0')
          throw new Error('Browser version must be stable X.Y.Z, each part <= 65535, and nonzero')
      } else throw new Error(`unsupported stage option ${args[index]}`)
    }
    stagePackage(args[0], args[1], includeTestPlatform, version)
  } else {
    throw new Error('usage: collector-contracts.mjs baseline | check [--base-ref REF] | stage <browser|system|reference-fixture> <output> [--include-current-test-platform] [--version X.Y.Z (browser only)]')
  }
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`)
  process.exitCode = 1
}
