// 折叠状态机（ADR-017 §3：collectors fold）——纯函数，事件进、快照出，无 chrome API 依赖。
//
// - 每个窗口各记其 active tab（忠实记录，不判操作系统前台；双时间线公理，ADR-017）。
//   多窗口时多段并存合法，windowId 进 Attributes 区分。
// - 进行中的活动持有稳定 Id：flush 推送 EndTime 单调生长的快照，
//   服务端按 Id upsert 收敛（ADR-018），跨上报周期不碎裂。
// - 单段生长逼近服务端 MaxDuration（24h）时轮换新 Id，防快照被校验丢弃。

import rotationPolicy from '../../../contracts/segment-rotation-policy.json'

export interface OpenActivity {
  id: string
  identityKey: string
  url: string
  title: string
  windowId: number
  startTime: number // epoch ms
}

/** 可 JSON 序列化的全量状态（存 chrome.storage.session，SW 重启不丢）。 */
export interface FoldState {
  open: Record<number, OpenActivity>
}

/** 上报形状，字段与 SegmentUploadRequest.ActivitySegmentItem 对齐（hub 反序列化大小写不敏感）。 */
export interface SegmentSnapshot {
  id: string
  source: 'browser'
  identityKey: string
  title: string
  startTime: string // ISO 8601
  endTime: string
  /** true 表示 Collector 已确认 Segment 结束；旧缓存缺席时按 false 提升。 */
  isFinal: boolean
  attributes: { url: string; domain: string; site: string; windowId: number }
}

export type FoldEvent =
  | { kind: 'activated'; windowId: number; url: string; title: string; at: number }
  | { kind: 'windowClosed'; windowId: number; at: number }

export interface FoldDeps {
  newId: () => string
  identityKeyOf: (url: string) => string
  domainOf: (url: string) => string
  /** 可注册域（深度表 v2 的 site 读数,ADR-030 §5）;空串 = 读数缺席。 */
  siteOf: (url: string) => string
}

export interface FoldResult {
  state: FoldState
  out: SegmentSnapshot[]
}

/** 轮换阈值：低于服务端 MaxDuration（24h），留出上报周期与时钟偏差余量。 */
export const ROTATE_AFTER_MS = rotationPolicy.rotateAfterMilliseconds

export function emptyState(): FoldState {
  return { open: {} }
}

export function applyEvent(state: FoldState, ev: FoldEvent, deps: FoldDeps): FoldResult {
  const cur = state.open[ev.windowId]

  if (ev.kind === 'windowClosed') {
    if (!cur) return { state, out: [] }
    const open = { ...state.open }
    delete open[ev.windowId]
    return { state: { open }, out: [snapshotOf(cur, ev.at, deps, true)] }
  }

  const key = deps.identityKeyOf(ev.url)

  // 同一活动（query/fragment 变化、标题变化）：不切段，只更新展示字段，
  // 最新 title/url 随下一次快照上行（服务端后写胜，ADR-018）。
  if (cur && cur.identityKey === key) {
    const open = { ...state.open, [ev.windowId]: { ...cur, url: ev.url, title: ev.title } }
    return { state: { open }, out: [] }
  }

  const out = cur ? [snapshotOf(cur, ev.at, deps, true)] : []
  const next: OpenActivity = {
    id: deps.newId(),
    identityKey: key,
    url: ev.url,
    title: ev.title,
    windowId: ev.windowId,
    startTime: ev.at,
  }
  return { state: { open: { ...state.open, [ev.windowId]: next } }, out }
}

/**
 * 周期快照：为每个进行中的活动发出 EndTime=now 的快照（Id 稳定，服务端 upsert 扩展边界）。
 * 超长活动就地轮换：旧段以最终快照封口，同一活动换新 Id 从 now 续记。
 */
export function flush(state: FoldState, now: number, deps: FoldDeps): FoldResult {
  const out: SegmentSnapshot[] = []
  let open = state.open
  let copied = false

  for (const [wid, a] of Object.entries(state.open)) {
    const isFinal = now - a.startTime >= ROTATE_AFTER_MS
    out.push(snapshotOf(a, now, deps, isFinal))
    if (isFinal) {
      if (!copied) {
        open = { ...open }
        copied = true
      }
      open[Number(wid)] = { ...a, id: deps.newId(), startTime: now }
    }
  }

  return { state: copied ? { open } : state, out }
}

function snapshotOf(a: OpenActivity, endMs: number, deps: FoldDeps, isFinal: boolean): SegmentSnapshot {
  return {
    id: a.id,
    source: 'browser',
    identityKey: a.identityKey,
    title: a.title,
    startTime: new Date(a.startTime).toISOString(),
    endTime: new Date(Math.max(endMs, a.startTime)).toISOString(),
    isFinal,
    attributes: { url: a.url, domain: deps.domainOf(a.url), site: deps.siteOf(a.url), windowId: a.windowId },
  }
}
