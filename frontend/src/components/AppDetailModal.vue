<script setup lang="ts">
import { computed, watch } from 'vue'
import { DialogRoot, DialogPortal, DialogOverlay, DialogContent, DialogTitle, DialogClose } from 'reka-ui'
import { fetchPublicSegments } from '../api/index'
import type { AppUsageResponse, SegmentResponse, DeviceInfoResponse } from '../api/index'
import type { CalendarContext } from '../calendar/localCalendarWindow'
import AppIcon from './AppIcon.vue'
import { runForCurrentIdentity, useAsyncData } from '../composables/useAsyncData'
import { formatDuration } from '../composables/useHeartbeat'
import { formatTitle } from '../titleFormatters'
import { upgradeBreakdown } from '../labelUpgrade'
import { toPluginSegs, toSystemSegs, toReplaySegs } from '../segmentAdapters'
import { envelope, buildTracks } from '../timeline/replayModel'
import type { Interval } from '../timeline/timelineModel'
import { niceTicks } from '../timeline/timeScale'
import { X } from 'lucide-vue-next'

const props = defineProps<{
  username: string
  deviceId: number
  calendarContext: Pick<CalendarContext, 'day' | 'correlationIdentity'>
  app: { appId: number; appName: string; totalSeconds: number }
  usageData: AppUsageResponse[]
  devices: DeviceInfoResponse[]
  isProvisional: boolean
}>()

const emit = defineEmits<{ close: [] }>()
const dayWindow = computed(() => props.calendarContext.day)

const dayBounds = computed<Interval>(() => ({
  start: Date.parse(dayWindow.value.start),
  end: Date.parse(dayWindow.value.endExclusive),
}))

// ── 插件段(非 system 轨) ──
const segs = useAsyncData<SegmentResponse[]>(() => {
  return fetchPublicSegments(props.username, {
    deviceId: props.deviceId,
    appId: props.app.appId,
    start: dayWindow.value.start,
    end: dayWindow.value.endExclusive,
  })
}, [], () => [props.deviceId, props.app.appId, dayWindow.value.start, dayWindow.value.endExclusive].join('|'))
const pluginSegments = segs.data
const loading = segs.pending
const segmentsFailed = computed(() => segs.error.value !== null)

// App Detail 只消费当前 Context 的 day + correlation identity，不拥有整页 Window Session。
const detailIdentity = computed(() => Object.freeze({
  appId: props.app.appId,
  deviceId: props.deviceId,
  refreshIdentity: props.calendarContext.correlationIdentity,
}))

watch(
  detailIdentity,
  () => runForCurrentIdentity(segs, () => detailIdentity.value),
  { immediate: true },
)

// ── 多轨回放（静态视窗 = 全部轨道数据的时间包络；模型在 timeline/replayModel.ts）──

const systemSegments = computed(() =>
  props.usageData.filter(u => u.appId === props.app.appId && u.startTime && u.endTime)
)

const viewBounds = computed(() => {
  const intervals: Interval[] = []
  for (const u of systemSegments.value) {
    intervals.push({ start: u.startTime!.getTime(), end: u.endTime!.getTime() })
  }
  for (const s of pluginSegments.value) {
    if (!s.startTime || !s.endTime) continue
    intervals.push({ start: s.startTime.getTime(), end: s.endTime.getTime() })
  }
  const content = envelope(intervals)
  if (!content) return null
  const start = Math.max(content.start, dayBounds.value.start)
  const end = Math.min(content.end, dayBounds.value.end)
  return end > start ? { start, end } : null
})

// 单设备和多设备使用同一套分组、轨道渲染；只有多组时显示设备名。
const deviceGroups = computed(() => {
  const vb = viewBounds.value
  if (!vb) return []
  const ids = new Set([...systemSegments.value, ...pluginSegments.value].map(s => s.deviceId ?? 0))
  const grouped = props.deviceId === 0 && ids.size > 1
  return (grouped ? [...ids].sort((a, b) => a - b) : [props.deviceId]).map(id => ({
    deviceId: id,
    deviceName: props.devices.find(d => d.id === id)?.name ?? `设备 ${id}`,
    tracks: buildTracks(toReplaySegs(
      grouped ? systemSegments.value.filter(s => (s.deviceId ?? 0) === id) : systemSegments.value,
      grouped ? pluginSegments.value.filter(s => (s.deviceId ?? 0) === id) : pluginSegments.value,
      dayBounds.value,
    ), vb, dayWindow.value.timeZone),
  })).filter(g => g.tracks.length)
})

// ── 标题明细（ADR-019 标签升级）──
// system 段有重叠插件段时标签升级为页面标题/URL，无覆盖的时间窗口 fallback 到窗口标题。

const breakdown = computed(() =>
  upgradeBreakdown(
    toSystemSegs(systemSegments.value, dayBounds.value),
    toPluginSegs(pluginSegments.value, dayBounds.value),
    formatTitle,
  )
)

const timeTicks = computed(() => {
  const vb = viewBounds.value
  return vb ? niceTicks(vb.start, vb.end, 10, dayWindow.value.timeZone) : []
})

// 排行入口在 DialogRoot 外，关闭后回到打开详情的按钮。
const returnFocus = document.activeElement as HTMLElement | null
</script>

<template>
  <DialogRoot :open="true" @update:open="!$event && emit('close')">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-50 bg-black/50 backdrop-blur-sm" />
      <DialogContent
        :aria-describedby="undefined"
        class="fixed left-1/2 top-1/2 z-50 flex max-h-[calc(100dvh_-_2rem)] w-[calc(100%_-_2rem)] max-w-4xl -translate-x-1/2 -translate-y-1/2 flex-col overflow-hidden rounded-xl border border-border bg-card shadow-2xl"
        @close-auto-focus.prevent="returnFocus?.focus()"
      >
        <!-- Header -->
        <div class="flex items-center gap-3 border-b border-border px-5 py-4">
          <AppIcon
            :username="username"
            :app-id="app.appId"
            class="h-7 w-7 rounded object-contain"
          />
          <DialogTitle class="truncate text-base font-semibold">{{ app.appName }}</DialogTitle>
          <span
            v-if="isProvisional"
            class="rounded-full border border-amber-400/30 bg-amber-400/10 px-2 py-0.5 text-[0.65rem] text-amber-200"
          >待归类</span>
          <span class="font-mono text-sm text-muted-foreground">{{ formatDuration(app.totalSeconds) }}</span>
          <DialogClose
            aria-label="关闭应用详情"
            class="ml-auto flex cursor-pointer items-center justify-center rounded-full p-1.5 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
          >
            <X :size="18" />
          </DialogClose>
        </div>

        <div class="flex flex-col gap-5 overflow-y-auto px-5 py-4">
          <!-- 多轨回放 -->
          <section>
            <h3 class="mb-2 text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">回放</h3>
            <div v-if="deviceGroups.length && viewBounds" class="overflow-hidden rounded-md border border-border bg-secondary">
              <!-- 刻度行 -->
              <div class="flex h-6 border-b border-border bg-muted">
                <div class="w-[80px] shrink-0 border-r border-border"></div>
                <div class="relative flex-1">
                  <span
                    v-for="t in timeTicks"
                    :key="t.at"
                    class="pointer-events-none absolute mt-0.5 -translate-x-1/2 font-mono text-[0.65rem] text-muted-foreground"
                    :style="{ left: t.percent + '%' }"
                  >{{ t.label }}</span>
                </div>
              </div>
              <!-- 设备分组回放:聚合视图下同一 App 在多台设备并行时,设备为最外层分组 -->
              <div v-for="g in deviceGroups" :key="g.deviceId">
                  <div v-if="deviceGroups.length > 1" class="flex h-6 items-center border-b border-border bg-muted/95 px-2">
                    <span class="truncate text-[0.7rem] font-semibold uppercase tracking-[0.06em] text-muted-foreground">
                      {{ g.deviceName }}
                    </span>
                  </div>
                  <div
                    v-for="track in g.tracks"
                    :key="track.source"
                    class="flex border-b border-border last:border-b-0"
                  >
                    <div class="flex w-[80px] shrink-0 items-center border-r border-border bg-muted px-2">
                      <span class="truncate font-mono text-[0.7rem] text-muted-foreground">{{ track.source }}</span>
                    </div>
                    <div class="flex-1">
                      <div
                        v-for="(lane, li) in track.lanes"
                        :key="li"
                        class="relative h-9 border-b border-dashed border-border/50 last:border-b-0"
                      >
                        <template v-for="(bar, i) in lane.bars" :key="i">
                          <div
                            v-if="bar.isPoint"
                            class="absolute top-1/2 z-[1] h-2 w-2 -translate-x-1/2 -translate-y-1/2 rotate-45 cursor-pointer bg-accent-3 hover:z-[2] hover:scale-125"
                            :style="{ left: bar.left + '%' }"
                            :title="bar.tooltip"
                          ></div>
                          <div
                            v-else
                            class="absolute top-2 h-5 cursor-pointer rounded-sm opacity-80 hover:z-[2] hover:opacity-100"
                            :class="track.source === 'system' ? 'bg-primary' : 'bg-accent-3'"
                            :style="{ left: bar.left + '%', width: bar.width + '%' }"
                            :title="bar.tooltip"
                          ></div>
                        </template>
                      </div>
                    </div>
                  </div>
              </div>
            </div>
            <div v-else class="rounded-md border border-border bg-secondary py-6 text-center text-[0.8rem] text-muted-foreground">
              {{ loading ? '加载中…' : segmentsFailed ? '回放数据加载失败' : '当日无回放数据' }}
            </div>
          </section>

          <!-- 标题明细（插件覆盖时段升级为页面级，其余窗口标题 fallback） -->
          <section>
            <h3 class="mb-2 text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">标题明细</h3>
            <div class="flex flex-col gap-2">
              <div
                v-for="(t, ti) in breakdown"
                :key="ti"
                class="flex items-center gap-2 text-[0.8rem]"
                :class="t.category === 'system' ? 'opacity-50' : ''"
              >
                <span
                  v-if="t.upgraded"
                  class="h-1.5 w-1.5 shrink-0 rounded-full bg-accent-3"
                  title="页面级明细（浏览器插件）"
                ></span>
                <span class="flex min-w-0 flex-1 flex-col">
                  <span class="truncate" :class="t.title ? '' : 'text-muted-foreground italic'" :title="t.title">{{ t.title || '无标题窗口' }}</span>
                  <span v-if="t.secondary" class="truncate text-[0.65rem] text-muted-foreground" :title="t.secondary">{{ t.secondary }}</span>
                </span>
                <span class="shrink-0 text-[0.7rem] text-muted-foreground">×{{ t.count }}</span>
                <span class="shrink-0 font-mono text-[0.75rem] text-muted-foreground">{{ formatDuration(t.totalSeconds) }}</span>
              </div>
              <div
                v-if="breakdown.length === 0"
                class="py-2 text-center text-[0.8rem] text-muted-foreground"
              >
                无标题明细
              </div>
            </div>
          </section>
        </div>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
