import { l as loadConfig } from "./assets/config-CudPlTIo.js";
const rotateAfterMilliseconds = 828e5;
const rotationPolicy = {
  rotateAfterMilliseconds
};
const ROTATE_AFTER_MS = rotationPolicy.rotateAfterMilliseconds;
function emptyState() {
  return { open: {} };
}
function applyEvent(state, ev, deps2) {
  const cur = state.open[ev.windowId];
  if (ev.kind === "windowClosed") {
    if (!cur) return { state, out: [] };
    const open = { ...state.open };
    delete open[ev.windowId];
    return { state: { open }, out: [snapshotOf(cur, ev.at, deps2, true)] };
  }
  const key = deps2.identityKeyOf(ev.url);
  if (cur && cur.identityKey === key) {
    const open = { ...state.open, [ev.windowId]: { ...cur, url: ev.url, title: ev.title } };
    return { state: { open }, out: [] };
  }
  const out = cur ? [snapshotOf(cur, ev.at, deps2, true)] : [];
  const next = {
    id: deps2.newId(),
    identityKey: key,
    url: ev.url,
    title: ev.title,
    windowId: ev.windowId,
    startTime: ev.at
  };
  return { state: { open: { ...state.open, [ev.windowId]: next } }, out };
}
function flush(state, now, deps2) {
  const out = [];
  let open = state.open;
  let copied = false;
  for (const [wid, a] of Object.entries(state.open)) {
    const isFinal = now - a.startTime >= ROTATE_AFTER_MS;
    out.push(snapshotOf(a, now, deps2, isFinal));
    if (isFinal) {
      if (!copied) {
        open = { ...open };
        copied = true;
      }
      open[Number(wid)] = { ...a, id: deps2.newId(), startTime: now };
    }
  }
  return { state: copied ? { open } : state, out };
}
function snapshotOf(a, endMs, deps2, isFinal) {
  return {
    id: a.id,
    source: "browser",
    identityKey: a.identityKey,
    title: a.title,
    startTime: new Date(a.startTime).toISOString(),
    endTime: new Date(Math.max(endMs, a.startTime)).toISOString(),
    isFinal,
    attributes: { url: a.url, domain: deps2.domainOf(a.url), site: deps2.siteOf(a.url), windowId: a.windowId }
  };
}
function identityKeyOf(rawUrl) {
  let u;
  try {
    u = new URL(rawUrl);
  } catch {
    return rawUrl;
  }
  if (u.origin === "null") {
    return u.href.split("#")[0].split("?")[0];
  }
  const path = u.pathname !== "/" && u.pathname.endsWith("/") ? u.pathname.slice(0, -1) : u.pathname;
  return u.origin + path;
}
function domainOf(rawUrl) {
  try {
    return new URL(rawUrl).hostname;
  } catch {
    return "";
  }
}
const MULTI_PART_SUFFIXES = /* @__PURE__ */ new Set([
  "com.cn",
  "net.cn",
  "org.cn",
  "gov.cn",
  "edu.cn",
  "ac.cn",
  "co.uk",
  "org.uk",
  "ac.uk",
  "gov.uk",
  "co.jp",
  "ne.jp",
  "or.jp",
  "ac.jp",
  "go.jp",
  "com.tw",
  "org.tw",
  "edu.tw",
  "com.hk",
  "org.hk",
  "edu.hk",
  "com.au",
  "net.au",
  "org.au",
  "edu.au",
  "co.kr",
  "or.kr",
  "ac.kr",
  "com.br",
  "org.br",
  "co.in",
  "org.in",
  "com.sg",
  "edu.sg"
]);
function siteOf(rawUrl) {
  let host;
  try {
    host = new URL(rawUrl).hostname;
  } catch {
    return "";
  }
  if (host.length === 0) return "";
  if (host.startsWith("[")) return host;
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host)) return host;
  const labels = host.split(".");
  if (labels.length === 1) return host;
  const lastTwo = labels.slice(-2).join(".");
  if (labels.length >= 3 && MULTI_PART_SUFFIXES.has(lastTwo)) return labels.slice(-3).join(".");
  return lastTwo;
}
function uuidv7(nowMs = Date.now()) {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  const ts = BigInt(nowMs);
  bytes[0] = Number(ts >> 40n & 0xffn);
  bytes[1] = Number(ts >> 32n & 0xffn);
  bytes[2] = Number(ts >> 24n & 0xffn);
  bytes[3] = Number(ts >> 16n & 0xffn);
  bytes[4] = Number(ts >> 8n & 0xffn);
  bytes[5] = Number(ts & 0xffn);
  bytes[6] = bytes[6] & 15 | 112;
  bytes[8] = bytes[8] & 63 | 128;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
function detectBrowserAppIdentity(signals) {
  if (signals.hasBraveApi) return void 0;
  const candidates = /* @__PURE__ */ new Set();
  for (const value of signals.brands ?? []) {
    const brand = value.trim().toLowerCase();
    if (brand === "google chrome") candidates.add("chrome");
    else if (brand === "microsoft edge") candidates.add("edge");
    else if (brand !== "chromium" && brand.replace(/[^a-z0-9]/g, "") !== "notabrand") return void 0;
  }
  const ua = signals.userAgent ?? "";
  if (/\b(?:OPR|Vivaldi|Firefox|EdgA|EdgiOS)\//i.test(ua)) return void 0;
  if (/\bEdg\//.test(ua)) candidates.add("edge");
  if (candidates.size !== 1) return void 0;
  const browser = [...candidates][0];
  if (signals.platform === "win") return browser === "chrome" ? "win:chrome" : "win:msedge";
  if (signals.platform === "mac") return browser === "chrome" ? "mac:com.google.chrome" : "mac:com.microsoft.edgemac";
  return void 0;
}
const ROUTE = "/v1/collector-protocol/external-host";
async function browserPackageReference() {
  const response = await fetch(chrome.runtime.getURL("collector-artifact-ref.json"));
  if (!response.ok) throw new Error("Browser Package metadata is unavailable");
  const metadata = await response.json();
  if (metadata?.packageId !== "heartbeat.collector.browser" || metadata.artifactId !== "browser.extension" || typeof metadata.packageVersion !== "string" || !/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(metadata.packageVersion) || ![metadata.packageContentHash, metadata.artifactHash].every((value) => typeof value === "string" && /^sha256:[0-9a-f]{64}$/.test(value))) {
    throw new Error("Browser Package metadata is invalid");
  }
  return metadata;
}
const DEFAULT_LIMITS = {
  maxFactsPerBatch: 500,
  maxBatchBytes: 1048576
};
const acknowledgedStatuses = /* @__PURE__ */ new Set(["committed", "duplicate", "superseded"]);
function isUuidV7(value) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}
function snapshotRevision(snapshot) {
  const revision = Date.parse(snapshot.endTime);
  return Number.isSafeInteger(revision) && revision > 0 ? revision : 1;
}
function toProtocolFact(snapshot, streamId) {
  if (!isUuidV7(snapshot.id)) return null;
  return {
    streamId,
    schemaRevision: 1,
    factId: snapshot.id,
    revision: snapshotRevision(snapshot),
    observedAt: null,
    recordState: "present",
    time: {
      start: snapshot.startTime,
      end: snapshot.endTime,
      isFinal: snapshot.isFinal
    },
    payload: {
      identityKey: snapshot.identityKey,
      title: snapshot.title,
      attributes: snapshot.attributes
    }
  };
}
function acknowledgedSnapshotIds(snapshots, acknowledgement) {
  return acknowledgement.results.filter(
    (result) => Number.isInteger(result.index) && result.index >= 0 && result.index < snapshots.length && acknowledgedStatuses.has(result.status)
  ).map((result) => snapshots[result.index].id);
}
async function openBrowserProtocolSession(port, appIdentityKey, externalHostIdentity, attempt, applySpec) {
  try {
    const hello = await fetch(`http://127.0.0.1:${port}${ROUTE}/hello`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(message(
        "heartbeat.collector.bootstrap/1",
        "activation.hello",
        attempt.helloMessageId,
        void 0,
        {
          ...await browserPackageReference(),
          protocolMajors: [1],
          supportedCapabilities: {
            "facts.segment": [1],
            "diagnostics.stream-gap": [1]
          },
          appIdentityKey,
          externalHostIdentity
        }
      ))
    });
    if (!hello.ok) return "rejected";
    const acceptedMessage = await hello.json();
    if (!isCorrelatedResponse(
      acceptedMessage,
      "heartbeat.collector.bootstrap/1",
      "activation.accepted",
      void 0,
      attempt.helloMessageId
    ) || !isUuidV7(acceptedMessage.body.activationId) || acceptedMessage.body.selectedProtocolMajor !== 1 || acceptedMessage.body.selectedCapabilities?.["facts.segment"] !== 1 || acceptedMessage.body.selectedCapabilities?.["diagnostics.stream-gap"] !== 1)
      return "rejected";
    const accepted = acceptedMessage.body;
    const initialize = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/initialize`,
      { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }
    );
    if (!initialize.ok) return "rejected";
    const initializeMessage = await initialize.json();
    if (!isCorrelatedResponse(
      initializeMessage,
      "heartbeat.collector/1",
      "activation.initialize",
      accepted.activationId,
      void 0
    )) return "rejected";
    const initialized = initializeMessage.body;
    if (initialized.spec.config.value.enabled === false) return "disabled";
    const flushPeriodMilliseconds = positiveInteger(initialized.spec.config.value.flushPeriodMs);
    if (flushPeriodMilliseconds === void 0 || flushPeriodMilliseconds < 3e4) return "rejected";
    if (positiveInteger(initialized.limits?.maxFactsPerBatch) === void 0 || positiveInteger(initialized.limits?.maxBatchBytes) === void 0)
      return "rejected";
    await applySpec?.({ enabled: true, flushPeriodMilliseconds });
    const initializedAck = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/initialized`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "activation.initialized",
          attempt.initializedMessageId,
          accepted.activationId,
          { appliedSpecRevision: initialized.spec.revision },
          initializeMessage.messageId
        ))
      }
    );
    if (!initializedAck.ok) return "rejected";
    const streams = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/streams`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "streams.open",
          attempt.streamsMessageId,
          accepted.activationId,
          {
            specRevision: initialized.spec.revision,
            bindings: [{ bindingId: "tabs", outputId: "activeTab", dimensions: {} }]
          }
        ))
      }
    );
    if (!streams.ok) return "rejected";
    const openedMessage = await streams.json();
    if (!isCorrelatedResponse(
      openedMessage,
      "heartbeat.collector/1",
      "streams.opened",
      accepted.activationId,
      attempt.streamsMessageId
    )) return "rejected";
    const opened = openedMessage.body;
    const stream = opened.streams.tabs;
    if (!stream?.streamId) return "rejected";
    const ready = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/ready`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "activation.ready",
          attempt.readyMessageId,
          accepted.activationId,
          {
            appliedSpecRevision: initialized.spec.revision
          }
        ))
      }
    );
    if (!ready.ok) return "rejected";
    const readyMessage = await ready.json();
    if (!isCorrelatedResponse(
      readyMessage,
      "heartbeat.collector/1",
      "activation.readyAck",
      accepted.activationId,
      attempt.readyMessageId
    )) return "rejected";
    const readyAcknowledgement = readyMessage.body;
    if (!readyAcknowledgement.lease?.token) return null;
    return {
      port,
      activationId: accepted.activationId,
      leaseToken: readyAcknowledgement.lease.token,
      streamId: stream.streamId,
      specRevision: initialized.spec.revision,
      expiresAt: readyAcknowledgement.lease.expiresAt,
      limits: normalizeLimits(initialized.limits),
      flushPeriodMilliseconds
    };
  } catch {
    return null;
  }
}
async function renewBrowserProtocolSession(session) {
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/renew`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ leaseToken: session.leaseToken })
      }
    );
    if (!response.ok) return null;
    const lease = await response.json();
    return lease.token === session.leaseToken && typeof lease.expiresAt === "string" ? { ...session, expiresAt: lease.expiresAt } : null;
  } catch {
    return null;
  }
}
async function publishBrowserFacts(session, snapshots, previousAttempt, persistAttempt) {
  const limits = normalizeLimits(session.limits);
  const maxFacts = Math.max(1, Math.min(limits.maxFactsPerBatch, 500));
  const reusableAttempt = previousAttempt?.activationId === session.activationId ? previousAttempt : void 0;
  const batch = reusableAttempt?.snapshots ?? takeBatchWithinByteLimit(snapshots, session, maxFacts);
  if (snapshots.length > 0 && batch.length === 0) return { kind: "unavailable" };
  const facts = batch.map((snapshot) => toProtocolFact(snapshot, session.streamId));
  if (facts.some((fact) => fact === null)) return { kind: "unavailable", session };
  if (facts.length === 0) {
    return {
      kind: "acked",
      acknowledgedIds: [],
      acknowledgedRevisions: {},
      rejectedRevisions: {},
      session
    };
  }
  const attempt = reusableAttempt ?? { activationId: session.activationId, messageId: uuidv7(), snapshots: batch };
  await persistAttempt?.(attempt);
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/facts`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "facts.publish",
          attempt.messageId,
          session.activationId,
          {
            leaseToken: session.leaseToken,
            facts
          }
        ))
      }
    );
    if (response.status === 403) return { kind: "disabled" };
    if (!response.ok) return { kind: "unavailable" };
    const acknowledgementMessage = await response.json();
    if (!isCorrelatedResponse(
      acknowledgementMessage,
      "heartbeat.collector/1",
      "facts.ack",
      session.activationId,
      attempt.messageId
    ) || !hasCompleteFactResults(acknowledgementMessage.body, batch.length)) {
      throw new Error("facts.ack is malformed or does not match the publish attempt");
    }
    const acknowledgement = acknowledgementMessage.body;
    const acknowledgedIds = acknowledgedSnapshotIds(batch, acknowledgement);
    const rejected = acknowledgement.results.filter((result) => Number.isInteger(result.index) && result.index >= 0 && result.index < batch.length && result.status === "rejected");
    const retryResults = acknowledgement.results.filter((result) => result.status === "retry");
    const retries = retryResults.map((result) => positiveInteger(result.retryAfterMs) ?? 1e3);
    const nextPublishAttempt = retryResults.length === 0 ? void 0 : {
      activationId: session.activationId,
      messageId: uuidv7(),
      snapshots: retryResults.map((result) => batch[result.index])
    };
    if (nextPublishAttempt !== void 0) await persistAttempt?.(nextPublishAttempt);
    return {
      kind: "acked",
      acknowledgedIds,
      acknowledgedRevisions: Object.fromEntries(
        acknowledgedIds.map((id) => [
          id,
          snapshotRevision(batch.find((snapshot) => snapshot.id === id))
        ])
      ),
      rejectedRevisions: Object.fromEntries(rejected.map((result) => [
        batch[result.index].id,
        snapshotRevision(batch[result.index])
      ])),
      ...retries.length === 0 ? {} : { retryAfterMilliseconds: Math.max(...retries) },
      ...nextPublishAttempt === void 0 ? {} : { nextPublishAttempt },
      session
    };
  } catch {
    return { kind: "unavailable", publishAttempt: attempt, session };
  }
}
async function uploadWithBrowserProtocol(port, appIdentityKey, externalHostIdentity, snapshots, previousSession, previousActivationAttempt, previousPublishAttempt, persistActivationAttempt, persistPublishAttempt, applySpec, pendingGap, persistGapAttempt) {
  if (!appIdentityKey || !externalHostIdentity) return { kind: "unavailable" };
  if (snapshots.some((snapshot) => !isUuidV7(snapshot.id))) return { kind: "unavailable" };
  const renewed = previousSession?.port === port ? await renewBrowserProtocolSession(previousSession) : null;
  const activationAttempt = previousActivationAttempt ?? {
    helloMessageId: uuidv7(),
    initializedMessageId: uuidv7(),
    streamsMessageId: uuidv7(),
    readyMessageId: uuidv7()
  };
  if (renewed === null) await persistActivationAttempt?.(activationAttempt);
  const session = renewed ?? await openBrowserProtocolSession(
    port,
    appIdentityKey,
    externalHostIdentity,
    activationAttempt,
    applySpec
  );
  if (session === "disabled") return { kind: "disabled" };
  if (session === "rejected") return { kind: "unavailable" };
  if (session === null) return { kind: "unavailable", activationAttempt };
  let gapAcknowledged = false;
  if (pendingGap !== void 0) {
    const gapResult = await reportBrowserGap(session, pendingGap, persistGapAttempt);
    if (gapResult !== "acked") {
      return {
        kind: "unavailable",
        session
      };
    }
    gapAcknowledged = true;
  }
  const result = await publishBrowserFacts(
    session,
    snapshots,
    renewed === null && previousSession !== void 0 ? void 0 : previousPublishAttempt,
    persistPublishAttempt
  );
  return result.kind === "acked" || result.kind === "unavailable" ? { ...result, ...gapAcknowledged ? { gapAcknowledged: true } : {} } : result;
}
async function reportBrowserGap(session, gap, persistAttempt) {
  const attempt = gap.activationId === session.activationId && gap.messageId !== void 0 ? gap : { ...gap, activationId: session.activationId, messageId: uuidv7() };
  await persistAttempt?.(attempt);
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/gap`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message(
          "heartbeat.collector/1",
          "stream.gap",
          attempt.messageId,
          session.activationId,
          {
            leaseToken: session.leaseToken,
            streamId: session.streamId,
            gap: {
              gapId: attempt.gapId,
              start: attempt.start,
              end: attempt.end,
              reason: attempt.reason,
              estimatedFactsLost: attempt.estimatedFactsLost
            }
          }
        ))
      }
    );
    if (!response.ok) return "rejected";
    const acknowledgement = await response.json();
    return acknowledgement.protocol === "heartbeat.collector/1" && acknowledgement.type === "stream.gapAck" && acknowledgement.activationId === session.activationId && acknowledgement.replyTo === attempt.messageId && acknowledgement.body.streamId === session.streamId ? "acked" : "unavailable";
  } catch {
    return "unavailable";
  }
}
function takeBatchWithinByteLimit(snapshots, session, maxFacts) {
  const limit = normalizeLimits(session.limits).maxBatchBytes;
  const batch = [];
  for (const snapshot of snapshots.slice(0, maxFacts)) {
    const candidate = [...batch, snapshot];
    const facts = candidate.map((item) => toProtocolFact(item, session.streamId));
    const logicalMessage = {
      protocol: "heartbeat.collector/1",
      type: "facts.publish",
      messageId: "00000000-0000-7000-8000-000000000000",
      activationId: session.activationId,
      body: { facts }
    };
    if (dotNetJsonUpperBoundBytes(logicalMessage) > limit) {
      if (batch.length === 0) continue;
      break;
    }
    batch.push(snapshot);
  }
  return batch;
}
function dotNetJsonUpperBoundBytes(value) {
  const json = JSON.stringify(value);
  let bytes = 0;
  for (let index = 0; index < json.length; index += 1) {
    const code = json.charCodeAt(index);
    bytes += code > 127 || code === 43 || code === 60 || code === 62 || code === 38 || code === 39 ? 6 : 1;
  }
  return bytes;
}
function normalizeLimits(limits) {
  return {
    maxFactsPerBatch: positiveInteger(limits?.maxFactsPerBatch) ?? DEFAULT_LIMITS.maxFactsPerBatch,
    maxBatchBytes: positiveInteger(limits?.maxBatchBytes) ?? DEFAULT_LIMITS.maxBatchBytes
  };
}
function positiveInteger(value) {
  return Number.isSafeInteger(value) && Number(value) > 0 ? Number(value) : void 0;
}
function isCorrelatedResponse(response, protocol, type, activationId, replyTo) {
  return response?.protocol === protocol && response.type === type && isUuidV7(response.messageId) && response.activationId === activationId && response.replyTo === replyTo && response.body !== void 0;
}
function hasCompleteFactResults(acknowledgement, factCount) {
  if (!Array.isArray(acknowledgement?.results) || acknowledgement.results.length !== factCount)
    return false;
  const indices = acknowledgement.results.map((result) => result.index).sort((left, right) => left - right);
  if (!indices.every((index, position) => index === position)) return false;
  return acknowledgement.results.every((result) => {
    if (!["committed", "duplicate", "superseded", "rejected", "retry"].includes(result.status)) return false;
    if (result.status === "retry") return positiveInteger(result.retryAfterMs) !== void 0;
    return result.retryAfterMs === void 0;
  });
}
function message(protocol, type, messageId, activationId, body, replyTo) {
  return {
    protocol,
    type,
    messageId,
    ...activationId === void 0 ? {} : { activationId },
    ...replyTo === void 0 ? {} : { replyTo },
    body
  };
}
const PORT_RANGE = 10;
const PROBE_TIMEOUT_MS = 1500;
async function probeHub(port) {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/v1/collector-protocol/external-host`, {
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS)
    });
    if (!res.ok) return false;
    const body = await res.json();
    return body.binding === "external-host" && Array.isArray(body.protocolMajors) && body.protocolMajors.includes(1);
  } catch {
    return false;
  }
}
async function discoverHub(basePort) {
  const ports = Array.from({ length: PORT_RANGE }, (_, i) => basePort + i).filter(
    (p) => p <= 65535
  );
  const results = await Promise.all(ports.map(probeHub));
  const index = results.findIndex(Boolean);
  return index >= 0 ? ports[index] : null;
}
async function findCompatibleHub(basePort, targetPort) {
  if (await probeHub(targetPort)) return targetPort;
  return discoverHub(basePort);
}
class LoopbackBrowserHubAdapter {
  findCompatibleHub(basePort, targetPort) {
    return findCompatibleHub(basePort, targetPort);
  }
  deliverProtocol(request) {
    return uploadWithBrowserProtocol(
      request.port,
      request.appIdentityKey,
      request.externalHostIdentity,
      request.snapshots,
      request.previousSession,
      request.previousActivationAttempt,
      request.previousPublishAttempt,
      request.persistActivationAttempt,
      request.persistPublishAttempt,
      request.applySpec,
      request.pendingGap,
      request.persistGapAttempt
    );
  }
}
const DEFAULT_FLUSH_PERIOD_MS = 3e4;
const BACKOFF_BASE_MS = 3e4;
const BACKOFF_MAX_MS = 10 * 6e4;
const MAX_QUEUED = 5e3;
function createBrowserDelivery(dependencies) {
  const now = dependencies.now ?? Date.now;
  const warn = dependencies.warn ?? ((message2, error) => console.warn(message2, error ?? ""));
  let deliveryChain = Promise.resolve();
  function serialized2(operation) {
    const next = deliveryChain.then(operation, operation);
    deliveryChain = next.catch(() => {
    });
    return next;
  }
  async function policy() {
    return (await dependencies.store.loadDurable()).policy;
  }
  async function enqueueImplementation(snapshots) {
    if (snapshots.length === 0) return;
    const durable = await dependencies.store.loadDurable();
    const { queue, overflow } = enqueueBounded(durable.queue, snapshots);
    const next = {
      ...durable,
      queue,
      pendingGaps: appendBufferGap(durable.pendingGaps, overflow)
    };
    try {
      await dependencies.store.saveDurable(next);
    } catch (error) {
      warn("[heartbeat] outbox 写入失败，记录 Stream Gap", error);
      await dependencies.store.saveDurable({
        ...durable,
        pendingGaps: appendBufferGap(durable.pendingGaps, snapshots)
      });
    }
  }
  async function deliveryCycleImplementation() {
    let session = await dependencies.store.loadSession();
    let currentPolicy = (await dependencies.store.loadDurable()).policy;
    const appIdentityKey = await dependencies.loadAppIdentityKey();
    if (appIdentityKey === void 0) return currentPolicy;
    const attemptAt = now();
    if (attemptAt < session.backoff.nextAttemptAt) return currentPolicy;
    const basePort = await dependencies.loadBasePort();
    const targetPort = session.hubPort ?? basePort;
    const compatiblePort = await dependencies.hub.findCompatibleHub(basePort, targetPort);
    if (compatiblePort === null) {
      session = failWithBackoff(session, attemptAt);
      await dependencies.store.saveSession(session);
      return currentPolicy;
    }
    if (compatiblePort !== session.hubPort) {
      session = { ...session, hubPort: compatiblePort };
      await dependencies.store.saveSession(session);
    }
    const durable = await dependencies.store.loadDurable();
    const snapshots = Object.values(durable.queue);
    let reportedGap = durable.pendingGaps[0];
    const protocolResult = await dependencies.hub.deliverProtocol({
      port: compatiblePort,
      appIdentityKey,
      externalHostIdentity: await dependencies.loadExternalHostIdentity(),
      snapshots,
      previousSession: session.protocolSession,
      previousActivationAttempt: session.activationAttempt,
      previousPublishAttempt: relevantPublishAttempt(session.publishAttempt, durable.queue),
      persistActivationAttempt: async (attempt) => {
        session = { ...session, activationAttempt: attempt };
        await dependencies.store.saveSession(session);
      },
      persistPublishAttempt: async (attempt) => {
        session = { ...session, publishAttempt: attempt };
        await dependencies.store.saveSession(session);
      },
      applySpec: async (spec) => {
        currentPolicy = spec;
        await persistPolicy(dependencies.store, currentPolicy);
      },
      pendingGap: reportedGap,
      persistGapAttempt: async (attempt) => {
        reportedGap = attempt;
        const latest = await dependencies.store.loadDurable();
        await dependencies.store.saveDurable({
          ...latest,
          pendingGaps: replaceFirstGap(latest.pendingGaps, durable.pendingGaps[0], attempt)
        });
      }
    });
    if (protocolResult.kind === "acked") {
      await convergeProtocolAcknowledgement(
        dependencies.store,
        protocolResult,
        protocolResult.gapAcknowledged === true ? reportedGap : void 0,
        warn
      );
      session = {
        ...session,
        protocolSession: protocolResult.session,
        activationAttempt: void 0,
        publishAttempt: protocolResult.nextPublishAttempt,
        backoff: protocolResult.retryAfterMilliseconds === void 0 ? noBackoff() : { fails: 0, nextAttemptAt: attemptAt + protocolResult.retryAfterMilliseconds }
      };
      await dependencies.store.saveSession(session);
      return currentPolicy;
    }
    if (protocolResult.kind === "disabled") {
      session = {
        ...session,
        activationAttempt: void 0,
        publishAttempt: void 0
      };
      await dependencies.store.saveSession(session);
      currentPolicy = { ...currentPolicy, enabled: false };
      await persistPolicy(dependencies.store, currentPolicy);
      return currentPolicy;
    }
    if (protocolResult.kind === "unavailable") {
      if (protocolResult.gapAcknowledged === true && reportedGap !== void 0) {
        await removeAcknowledgedGap(dependencies.store, reportedGap);
      }
      session = {
        ...session,
        activationAttempt: protocolResult.activationAttempt,
        publishAttempt: protocolResult.publishAttempt,
        protocolSession: protocolResult.publishAttempt === void 0 ? void 0 : protocolResult.session
      };
      session = failWithBackoff(session, attemptAt);
      await dependencies.store.saveSession(session);
      return currentPolicy;
    }
    session = {
      ...session,
      protocolSession: void 0,
      activationAttempt: void 0,
      publishAttempt: void 0
    };
    await dependencies.store.saveSession(session);
    session = failWithBackoff(session, attemptAt);
    await dependencies.store.saveSession(session);
    return currentPolicy;
  }
  return {
    policy,
    enqueue: (snapshots) => serialized2(() => enqueueImplementation(snapshots)),
    deliveryCycle: () => serialized2(deliveryCycleImplementation)
  };
}
function enqueueBounded(current, snapshots) {
  const queue = { ...current };
  const overflow = [];
  let queuedCount = Object.keys(queue).length;
  for (const snapshot of snapshots) {
    if (queue[snapshot.id] === void 0 && queuedCount >= MAX_QUEUED) {
      overflow.push(snapshot);
      continue;
    }
    if (queue[snapshot.id] === void 0) queuedCount += 1;
    queue[snapshot.id] = snapshot;
  }
  return { queue, overflow };
}
function appendBufferGap(gaps, snapshots) {
  if (snapshots.length === 0) return gaps;
  return [...gaps, {
    gapId: uuidv7(),
    start: snapshots.reduce((earliest, item) => item.startTime < earliest ? item.startTime : earliest, snapshots[0].startTime),
    end: snapshots.reduce((latest, item) => item.endTime > latest ? item.endTime : latest, snapshots[0].endTime),
    reason: "buffer_overflow",
    estimatedFactsLost: snapshots.length
  }];
}
async function convergeProtocolAcknowledgement(store, result, acknowledgedGap, warn) {
  const durable = await store.loadDurable();
  const queue = { ...durable.queue };
  const rejected = [];
  for (const [id, snapshot] of Object.entries(queue)) {
    const revision = snapshotRevision(snapshot);
    if (result.rejectedRevisions[id] === revision) {
      rejected.push(snapshot);
      delete queue[id];
    } else if (result.acknowledgedRevisions[id] === revision) {
      delete queue[id];
    }
  }
  if (rejected.length > 0) {
    warn(`[heartbeat] ${rejected.length} 条 Fact 被 Hub 永久拒绝，已移入诊断 dead-letter`);
  }
  await store.saveDurable({
    ...durable,
    queue,
    pendingGaps: acknowledgedGap === void 0 ? durable.pendingGaps : removeGap(durable.pendingGaps, acknowledgedGap),
    deadLetters: [...durable.deadLetters, ...rejected].slice(-100)
  });
}
async function removeAcknowledgedGap(store, acknowledged) {
  const durable = await store.loadDurable();
  await store.saveDurable({
    ...durable,
    pendingGaps: removeGap(durable.pendingGaps, acknowledged)
  });
}
function replaceFirstGap(gaps, expected, replacement) {
  if (expected === void 0) return gaps;
  const index = gaps.findIndex((gap) => sameGap(gap, expected));
  if (index < 0) return gaps;
  return gaps.map((gap, position) => position === index ? replacement : gap);
}
function removeGap(gaps, acknowledged) {
  const index = gaps.findIndex((gap) => sameGap(gap, acknowledged));
  return index < 0 ? gaps : gaps.filter((_, position) => position !== index);
}
function sameGap(left, right) {
  return left.gapId === right.gapId;
}
function relevantPublishAttempt(attempt, queue) {
  if (attempt === void 0) return void 0;
  return attempt.snapshots.some(
    (snapshot) => queue[snapshot.id] !== void 0 && snapshotRevision(queue[snapshot.id]) === snapshotRevision(snapshot)
  ) ? attempt : void 0;
}
function noBackoff() {
  return { fails: 0, nextAttemptAt: 0 };
}
function failWithBackoff(session, now) {
  const fails = session.backoff.fails + 1;
  const delay = Math.min(BACKOFF_BASE_MS * 2 ** (fails - 1), BACKOFF_MAX_MS);
  return { ...session, backoff: { fails, nextAttemptAt: now + delay } };
}
const defaultBrowserDeliverySession = () => ({
  backoff: noBackoff()
});
const emptyBrowserDeliveryDurableState = () => ({
  queue: {},
  pendingGaps: [],
  deadLetters: [],
  policy: { enabled: true, flushPeriodMilliseconds: DEFAULT_FLUSH_PERIOD_MS }
});
async function persistPolicy(store, policy) {
  const durable = await store.loadDurable();
  await store.saveDurable({ ...durable, policy });
}
const QUEUE_KEY = "pendingSegments";
const BACKOFF_KEY = "backoff";
const HUB_PORT_KEY = "hubPort";
const PROTOCOL_SESSION_KEY = "collectorProtocolSession";
const PROTOCOL_ACTIVATION_ATTEMPT_KEY = "collectorProtocolActivationAttempt";
const PROTOCOL_PUBLISH_ATTEMPT_KEY = "collectorProtocolPublishAttempt";
const FLUSH_PERIOD_KEY = "browserCollectorFlushPeriodMs";
const DEAD_LETTER_KEY = "browserCollectorDeadLetters";
const PENDING_GAP_KEY = "browserCollectorPendingGap";
const DESIRED_ENABLED_KEY = "browserCollectorDesiredEnabled";
const DELIVERY_POLICY_KEY = "browserCollectorDeliveryPolicy";
const EXTERNAL_HOST_IDENTITY_KEY = "browserCollectorExternalHostIdentity";
class ChromeBrowserDeliveryStore {
  sessionStarted = false;
  async loadDurable() {
    const [local, transient] = await Promise.all([
      chrome.storage.local.get([
        QUEUE_KEY,
        PENDING_GAP_KEY,
        DEAD_LETTER_KEY,
        DELIVERY_POLICY_KEY
      ]),
      chrome.storage.session.get([DESIRED_ENABLED_KEY, FLUSH_PERIOD_KEY])
    ]);
    const defaults = emptyBrowserDeliveryDurableState();
    const rawQueue = isRecord(local[QUEUE_KEY]) ? local[QUEUE_KEY] : {};
    const rawGaps = local[PENDING_GAP_KEY];
    const policy = normalizePolicy(
      local[DELIVERY_POLICY_KEY],
      transient[DESIRED_ENABLED_KEY],
      transient[FLUSH_PERIOD_KEY]
    );
    const pendingGaps = normalizePendingGaps(rawGaps);
    if (pendingGaps.migrated) {
      await chrome.storage.local.set({ [PENDING_GAP_KEY]: pendingGaps.value });
    }
    return {
      queue: normalizeQueuedSnapshots(rawQueue),
      pendingGaps: pendingGaps.value,
      deadLetters: Array.isArray(local[DEAD_LETTER_KEY]) ? local[DEAD_LETTER_KEY] : defaults.deadLetters,
      policy
    };
  }
  async saveDurable(state) {
    await chrome.storage.local.set({
      [QUEUE_KEY]: state.queue,
      [PENDING_GAP_KEY]: state.pendingGaps,
      [DEAD_LETTER_KEY]: state.deadLetters,
      [DELIVERY_POLICY_KEY]: state.policy
    });
  }
  async loadSession() {
    if (!this.sessionStarted) {
      await chrome.storage.session.remove([
        PROTOCOL_SESSION_KEY,
        PROTOCOL_ACTIVATION_ATTEMPT_KEY,
        PROTOCOL_PUBLISH_ATTEMPT_KEY
      ]);
      this.sessionStarted = true;
    }
    const got = await chrome.storage.session.get([
      BACKOFF_KEY,
      HUB_PORT_KEY,
      PROTOCOL_SESSION_KEY,
      PROTOCOL_ACTIVATION_ATTEMPT_KEY,
      PROTOCOL_PUBLISH_ATTEMPT_KEY
    ]);
    const defaults = defaultBrowserDeliverySession();
    return {
      backoff: normalizeBackoff(got[BACKOFF_KEY]) ?? defaults.backoff,
      ...positivePort(got[HUB_PORT_KEY]) === void 0 ? {} : { hubPort: Number(got[HUB_PORT_KEY]) },
      ...got[PROTOCOL_SESSION_KEY] === void 0 ? {} : { protocolSession: got[PROTOCOL_SESSION_KEY] },
      ...got[PROTOCOL_ACTIVATION_ATTEMPT_KEY] === void 0 ? {} : { activationAttempt: got[PROTOCOL_ACTIVATION_ATTEMPT_KEY] },
      ...got[PROTOCOL_PUBLISH_ATTEMPT_KEY] === void 0 ? {} : { publishAttempt: got[PROTOCOL_PUBLISH_ATTEMPT_KEY] }
    };
  }
  async saveSession(state) {
    await chrome.storage.session.set({
      [BACKOFF_KEY]: state.backoff,
      ...state.hubPort === void 0 ? {} : { [HUB_PORT_KEY]: state.hubPort },
      ...state.protocolSession === void 0 ? {} : { [PROTOCOL_SESSION_KEY]: state.protocolSession },
      ...state.activationAttempt === void 0 ? {} : { [PROTOCOL_ACTIVATION_ATTEMPT_KEY]: state.activationAttempt },
      ...state.publishAttempt === void 0 ? {} : { [PROTOCOL_PUBLISH_ATTEMPT_KEY]: state.publishAttempt }
    });
    const remove = [
      ...state.hubPort === void 0 ? [HUB_PORT_KEY] : [],
      ...state.protocolSession === void 0 ? [PROTOCOL_SESSION_KEY] : [],
      ...state.activationAttempt === void 0 ? [PROTOCOL_ACTIVATION_ATTEMPT_KEY] : [],
      ...state.publishAttempt === void 0 ? [PROTOCOL_PUBLISH_ATTEMPT_KEY] : []
    ];
    if (remove.length > 0) await chrome.storage.session.remove(remove);
    this.sessionStarted = true;
  }
}
function normalizePendingGaps(raw) {
  const gaps = Array.isArray(raw) ? raw : raw === void 0 ? [] : [raw];
  let migrated = false;
  const value = gaps.map((gap) => {
    if (typeof gap.gapId === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(gap.gapId)) {
      return gap;
    }
    migrated = true;
    return { ...gap, gapId: uuidv7() };
  });
  return { value, migrated };
}
function createChromeBrowserDelivery() {
  return createBrowserDelivery({
    store: new ChromeBrowserDeliveryStore(),
    hub: new LoopbackBrowserHubAdapter(),
    loadAppIdentityKey: async () => {
      const nav = navigator;
      const platform = await chrome.runtime.getPlatformInfo();
      return detectBrowserAppIdentity({
        platform: platform.os,
        brands: nav.userAgentData?.brands?.map((item) => item.brand),
        userAgent: nav.userAgent,
        hasBraveApi: nav.brave !== void 0
      });
    },
    loadBasePort: async () => (await loadConfig()).port,
    loadExternalHostIdentity
  });
}
async function loadExternalHostIdentity() {
  const stored = await chrome.storage.local.get(EXTERNAL_HOST_IDENTITY_KEY);
  const existing = stored[EXTERNAL_HOST_IDENTITY_KEY];
  if (typeof existing === "string" && existing.length > 0) return existing;
  const created = crypto.randomUUID();
  await chrome.storage.local.set({ [EXTERNAL_HOST_IDENTITY_KEY]: created });
  return created;
}
function normalizeQueuedSnapshots(stored) {
  return Object.fromEntries(
    Object.entries(stored).map(([id, snapshot]) => [id, {
      id: snapshot.id,
      source: snapshot.source,
      identityKey: snapshot.identityKey,
      title: snapshot.title,
      startTime: snapshot.startTime,
      endTime: snapshot.endTime,
      isFinal: snapshot.isFinal === true,
      attributes: snapshot.attributes
    }])
  );
}
function normalizePolicy(durable, legacyEnabled, legacyFlushPeriod) {
  if (isRecord(durable)) {
    const flushPeriodMilliseconds = positiveFlushPeriod(durable.flushPeriodMilliseconds);
    if (typeof durable.enabled === "boolean" && flushPeriodMilliseconds !== void 0) {
      return { enabled: durable.enabled, flushPeriodMilliseconds };
    }
  }
  return {
    enabled: legacyEnabled !== false,
    flushPeriodMilliseconds: positiveFlushPeriod(legacyFlushPeriod) ?? 3e4
  };
}
function normalizeBackoff(value) {
  if (!isRecord(value)) return void 0;
  const fails = Number(value.fails);
  const nextAttemptAt = Number(value.nextAttemptAt);
  return Number.isSafeInteger(fails) && fails >= 0 && Number.isSafeInteger(nextAttemptAt) && nextAttemptAt >= 0 ? { fails, nextAttemptAt } : void 0;
}
function positiveFlushPeriod(value) {
  const number = Number(value);
  return Number.isSafeInteger(number) && number >= 3e4 ? number : void 0;
}
function positivePort(value) {
  const number = Number(value);
  return Number.isSafeInteger(number) && number > 0 && number <= 65535 ? number : void 0;
}
function isRecord(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
const STATE_KEY = "foldState";
const ALARM_NAME = "heartbeat-flush";
const deps = {
  newId: uuidv7,
  identityKeyOf,
  domainOf,
  siteOf
};
const delivery = createChromeBrowserDelivery();
let chain = Promise.resolve();
function serialized(fn) {
  const next = chain.then(fn, fn);
  chain = next.catch(() => {
  });
  return next;
}
async function loadState() {
  const got = await chrome.storage.session.get(STATE_KEY);
  return got[STATE_KEY] ?? emptyState();
}
async function saveState(state) {
  await chrome.storage.session.set({ [STATE_KEY]: state });
}
async function handleEvent(ev) {
  if (!(await delivery.policy()).enabled) return;
  const state = await loadState();
  const { state: next, out } = applyEvent(state, ev, deps);
  if (next !== state) await saveState(next);
  await delivery.enqueue(out);
}
async function flushAndUpload() {
  const before = await delivery.policy();
  if (before.enabled) {
    const state = await loadState();
    const { state: next, out } = flush(state, Date.now(), deps);
    if (next !== state) await saveState(next);
    await delivery.enqueue(out);
  }
  const after = await delivery.deliveryCycle();
  await applyDeliveryPolicy(before, after);
}
async function applyDeliveryPolicy(before, after) {
  chrome.alarms.create(ALARM_NAME, {
    periodInMinutes: after.flushPeriodMilliseconds / 6e4
  });
  if (!after.enabled) {
    await saveState(emptyState());
  } else if (!before.enabled) {
    await saveState(emptyState());
    await reconcile();
  }
}
async function reconcile() {
  if (!(await delivery.policy()).enabled) return;
  const tabs = await chrome.tabs.query({ active: true });
  const liveWindows = new Set(tabs.map((t) => t.windowId));
  const now = Date.now();
  const state = await loadState();
  for (const wid of Object.keys(state.open).map(Number)) {
    if (!liveWindows.has(wid)) await handleEvent({ kind: "windowClosed", windowId: wid, at: now });
  }
  for (const t of tabs) {
    if (t.url && t.windowId !== void 0) {
      await handleEvent({ kind: "activated", windowId: t.windowId, url: t.url, title: t.title ?? "", at: now });
    }
  }
}
chrome.tabs.onActivated.addListener(({ tabId, windowId }) => {
  void serialized(async () => {
    const tab = await chrome.tabs.get(tabId).catch(() => null);
    if (!tab?.url) return;
    await handleEvent({ kind: "activated", windowId, url: tab.url, title: tab.title ?? "", at: Date.now() });
  });
});
chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (!tab.active || !tab.url) return;
  if (changeInfo.url === void 0 && changeInfo.title === void 0) return;
  void serialized(
    () => handleEvent({ kind: "activated", windowId: tab.windowId, url: tab.url, title: tab.title ?? "", at: Date.now() })
  );
});
chrome.windows.onRemoved.addListener((windowId) => {
  void serialized(() => handleEvent({ kind: "windowClosed", windowId, at: Date.now() }));
});
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === ALARM_NAME) void serialized(flushAndUpload);
});
void serialized(async () => {
  const current = await delivery.policy();
  chrome.alarms.create(ALARM_NAME, {
    periodInMinutes: current.flushPeriodMilliseconds / 6e4
  });
  if (!current.enabled) await saveState(emptyState());
  else await reconcile();
});
