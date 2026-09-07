// Service worker：chrome 事件 → 折叠纯函数 → 队列 → 周期上报 loopback hub。
//
// MV3 SW 随时可能被杀：折叠状态存 chrome.storage.session（浏览器会话内跨 SW 重启存活，
// 浏览器退出即清——进行中活动的快照已生长到最后一次 flush，行自然封口，损失 ≤ 一个上报周期）。
// 待传队列存 chrome.storage.local（跨浏览器重启存活，Agent 未运行时不丢数据）。

import {
  applyEvent,
  emptyState,
  flush,
  type FoldDeps,
  type FoldEvent,
  type FoldState,
} from './fold'
import { domainOf, identityKeyOf, siteOf } from './normalize'
import { uuidv7 } from './ids'
import { createChromeBrowserDelivery } from './delivery-chrome'
import type { BrowserCollectionPolicy } from './delivery'

const STATE_KEY = 'foldState'
const ALARM_NAME = 'heartbeat-flush'

const deps: FoldDeps = {
  newId: uuidv7,
  identityKeyOf,
  domainOf,
  siteOf,
}

const delivery = createChromeBrowserDelivery()

// ---- 串行化：storage 读改写不可交错（事件处理与 flush 共享折叠状态）。----

let chain: Promise<unknown> = Promise.resolve()

function serialized<T>(fn: () => Promise<T>): Promise<T> {
  const next = chain.then(fn, fn)
  chain = next.catch(() => {})
  return next
}

// ---- 折叠状态（交付持久化由 BrowserDelivery 拥有）----

async function loadState(): Promise<FoldState> {
  const got = await chrome.storage.session.get(STATE_KEY)
  return (got[STATE_KEY] as FoldState | undefined) ?? emptyState()
}

async function saveState(state: FoldState): Promise<void> {
  await chrome.storage.session.set({ [STATE_KEY]: state })
}

// ---- 事件处理 ----

async function handleEvent(ev: FoldEvent): Promise<void> {
  if (!(await delivery.policy()).enabled) return
  const state = await loadState()
  const { state: next, out } = applyEvent(state, ev, deps)
  if (next !== state) await saveState(next)
  await delivery.enqueue(out)
}

async function flushAndUpload(): Promise<void> {
  const before = await delivery.policy()
  if (before.enabled) {
    const state = await loadState()
    const { state: next, out } = flush(state, Date.now(), deps)
    if (next !== state) await saveState(next)
    await delivery.enqueue(out)
  }
  const after = await delivery.deliveryCycle()
  await applyDeliveryPolicy(before, after)
}

async function applyDeliveryPolicy(
  before: BrowserCollectionPolicy,
  after: BrowserCollectionPolicy,
): Promise<void> {
  chrome.alarms.create(ALARM_NAME, {
    periodInMinutes: after.flushPeriodMilliseconds / 60_000,
  })
  if (!after.enabled) {
    // 已知停用跨浏览器重启保留；fold state 必须同时封死，outbox 则继续保留。
    await saveState(emptyState())
  } else if (!before.enabled) {
    // 重新启用从当前 tab 新开活动，不把停用区间补进旧 Segment。
    await saveState(emptyState())
    await reconcile()
  }
}

/**
 * SW 唤醒对账：以"当前各窗口的 active tab"为真源重放一次。
 * 幂等——同 identityKey 不产生边界；已消失窗口的活动就地封口。
 */
async function reconcile(): Promise<void> {
  if (!(await delivery.policy()).enabled) return
  const tabs = await chrome.tabs.query({ active: true })
  const liveWindows = new Set(tabs.map((t) => t.windowId))
  const now = Date.now()

  const state = await loadState()
  for (const wid of Object.keys(state.open).map(Number)) {
    if (!liveWindows.has(wid)) await handleEvent({ kind: 'windowClosed', windowId: wid, at: now })
  }
  for (const t of tabs) {
    if (t.url && t.windowId !== undefined) {
      await handleEvent({ kind: 'activated', windowId: t.windowId, url: t.url, title: t.title ?? '', at: now })
    }
  }
}

// ---- 接线（顶层同步注册，MV3 要求）----

chrome.tabs.onActivated.addListener(({ tabId, windowId }) => {
  void serialized(async () => {
    const tab = await chrome.tabs.get(tabId).catch(() => null)
    if (!tab?.url) return
    await handleEvent({ kind: 'activated', windowId, url: tab.url, title: tab.title ?? '', at: Date.now() })
  })
})

chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  // 只关心"当前 active tab 的身份/标题变化"；后台 tab 的加载与本采集器无关。
  if (!tab.active || !tab.url) return
  if (changeInfo.url === undefined && changeInfo.title === undefined) return
  void serialized(() =>
    handleEvent({ kind: 'activated', windowId: tab.windowId, url: tab.url!, title: tab.title ?? '', at: Date.now() }),
  )
})

chrome.windows.onRemoved.addListener((windowId) => {
  void serialized(() => handleEvent({ kind: 'windowClosed', windowId, at: Date.now() }))
})

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === ALARM_NAME) void serialized(flushAndUpload)
})

// 每次 SW 唤醒都执行（幂等）：按持久 policy 恢复闹钟与 fold 状态，再对账。
void serialized(async () => {
  const current = await delivery.policy()
  chrome.alarms.create(ALARM_NAME, {
    periodInMinutes: current.flushPeriodMilliseconds / 60_000,
  })
  if (!current.enabled) await saveState(emptyState())
  else await reconcile()
})
