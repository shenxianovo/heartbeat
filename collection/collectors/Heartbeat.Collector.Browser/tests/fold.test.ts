import { describe, expect, it } from 'vitest'
import {
  applyEvent,
  emptyState,
  flush,
  ROTATE_AFTER_MS,
  type FoldDeps,
} from '../src/fold'
import { identityKeyOf, domainOf, siteOf } from '../src/normalize'

function makeDeps(): FoldDeps {
  let n = 0
  return {
    newId: () => `id-${++n}`,
    identityKeyOf,
    domainOf,
    siteOf,
  }
}

const T0 = Date.UTC(2026, 6, 5, 12, 0, 0)

function activated(windowId: number, url: string, at: number, title = 'page') {
  return { kind: 'activated' as const, windowId, url, title, at }
}

describe('applyEvent', () => {
  it('首个激活开启活动，不立即产出快照', () => {
    const deps = makeDeps()
    const { state, out } = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    expect(out).toHaveLength(0)
    expect(state.open[1]).toMatchObject({ id: 'id-1', identityKey: 'https://a.com/x', startTime: T0 })
  })

  it('同一 identityKey（query 变化/标题变化）不切段，只更新展示字段', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://a.com/x?utm=1', T0, 'old'), deps)
    r = applyEvent(r.state, activated(1, 'https://a.com/x?utm=2', T0 + 1000, 'new'), deps)
    expect(r.out).toHaveLength(0)
    expect(r.state.open[1].id).toBe('id-1') // 段身份未变
    expect(r.state.open[1].title).toBe('new')
    expect(r.state.open[1].url).toBe('https://a.com/x?utm=2')
  })

  it('identityKey 变化：封口旧段、开启新段（新 Id）', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    r = applyEvent(r.state, activated(1, 'https://b.com/y', T0 + 5000), deps)

    expect(r.out).toHaveLength(1)
    expect(r.out[0]).toMatchObject({
      id: 'id-1',
      source: 'browser',
      identityKey: 'https://a.com/x',
        startTime: new Date(T0).toISOString(),
      endTime: new Date(T0 + 5000).toISOString(),
      isFinal: true,
    })
    expect(r.state.open[1]).toMatchObject({ id: 'id-2', identityKey: 'https://b.com/y' })
  })

  it('多窗口各自持有活动，互不干扰（windowId 进 attributes）', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    r = applyEvent(r.state, activated(2, 'https://b.com/y', T0 + 100), deps)
    expect(r.out).toHaveLength(0)
    expect(Object.keys(r.state.open)).toHaveLength(2)

    const flushed = flush(r.state, T0 + 60_000, deps)
    expect(flushed.out).toHaveLength(2)
    const byWindow = new Map(flushed.out.map((s) => [s.attributes.windowId, s]))
    expect(byWindow.get(1)?.identityKey).toBe('https://a.com/x')
    expect(byWindow.get(2)?.identityKey).toBe('https://b.com/y')
  })

  it('窗口关闭封口该窗口的活动', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    const closed = applyEvent(r.state, { kind: 'windowClosed', windowId: 1, at: T0 + 3000 }, deps)
    expect(closed.out).toHaveLength(1)
    expect(closed.out[0].endTime).toBe(new Date(T0 + 3000).toISOString())
    expect(closed.out[0].isFinal).toBe(true)
    expect(closed.state.open[1]).toBeUndefined()
  })

  it('关闭无活动的窗口是空操作', () => {
    const deps = makeDeps()
    const r = applyEvent(emptyState(), { kind: 'windowClosed', windowId: 9, at: T0 }, deps)
    expect(r.out).toHaveLength(0)
  })
})

describe('flush（ADR-018 稳定 Id 快照）', () => {
  it('连续两次 flush：同一 Id，EndTime 单调生长', () => {
    const deps = makeDeps()
    const { state } = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)

    const f1 = flush(state, T0 + 30_000, deps)
    const f2 = flush(f1.state, T0 + 60_000, deps)

    expect(f1.out[0].id).toBe('id-1')
    expect(f2.out[0].id).toBe('id-1')
    expect(f1.out[0].startTime).toBe(f2.out[0].startTime)
    expect(f1.out[0].isFinal).toBe(false)
    expect(new Date(f2.out[0].endTime).getTime()).toBeGreaterThan(new Date(f1.out[0].endTime).getTime())
  })

  it('快照携带完整原始 URL、domain 与 site（无损原则 + 深度表 v2 运输槽）', () => {
    const deps = makeDeps()
    const { state } = applyEvent(
      emptyState(),
      activated(1, 'https://www.youtube.com/watch?v=abc#t=10', T0),
      deps,
    )
    const f = flush(state, T0 + 1000, deps)
    expect(f.out[0].attributes).toEqual({
      url: 'https://www.youtube.com/watch?v=abc#t=10',
      domain: 'www.youtube.com',
      site: 'youtube.com',
      windowId: 1,
    })
  })

  it('段只携带观测内容，App 身份属于 Stream', () => {
    const deps = makeDeps()
    const { state } = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    const [snapshot] = flush(state, T0 + 1000, deps).out

    expect(snapshot).not.toHaveProperty('appHint')
    expect(snapshot).toMatchObject({
      source: 'browser',
      identityKey: 'https://a.com/x',
      title: 'page',
    })
  })

  it('空状态 flush 无产出且状态不变', () => {
    const deps = makeDeps()
    const s = emptyState()
    const f = flush(s, T0, deps)
    expect(f.out).toHaveLength(0)
    expect(f.state).toBe(s)
  })

  it('超长活动轮换：旧段封口、同活动换新 Id 续记（防超服务端 MaxDuration 被丢）', () => {
    const deps = makeDeps()
    const { state } = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)

    const rotateAt = T0 + ROTATE_AFTER_MS
    const f = flush(state, rotateAt, deps)

    expect(f.out).toHaveLength(1)
    expect(f.out[0].id).toBe('id-1') // 旧段最终快照
    expect(f.out[0].endTime).toBe(new Date(rotateAt).toISOString())
    expect(f.out[0].isFinal).toBe(true)

    const rotated = f.state.open[1]
    expect(rotated.id).toBe('id-2') // 新 Id 从 now 续记
    expect(rotated.startTime).toBe(rotateAt)
    expect(rotated.identityKey).toBe('https://a.com/x') // 活动身份不变

    // 轮换后的下一次 flush 用新 Id
    const f2 = flush(f.state, rotateAt + 30_000, deps)
    expect(f2.out[0].id).toBe('id-2')
    expect(f2.out[0].isFinal).toBe(false)
  })
})
