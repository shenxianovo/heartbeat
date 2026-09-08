<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import type { AppUsageResponse, DeviceInfoResponse } from '../api/index'
import type { CalendarWindowEnvelope } from '../calendar/localCalendarWindow'
import { useTimelineDrag } from '../composables/useTimelineDrag'
import AppIcon from './AppIcon.vue'
import DashboardCard from './DashboardCard.vue'
import { LayoutGrid, AlignJustify } from 'lucide-vue-next'
import {
  parseUsage,
  buildRows,
  buildDayHourBins,
  mergeActivityBursts,
  initialViewBounds,
  groupByDevice,
  projectInterval,
  type Interval,
} from '../timeline/timelineModel'
import { niceTicks } from '../timeline/timeScale'

const props = defineProps<{
  loading?: boolean,
  username: string,
  usageData: AppUsageResponse[],
  appNameMap: Map<number, string>,
  dayWindow: CalendarWindowEnvelope<'day'>,
  isToday: boolean,
  devices: DeviceInfoResponse[],
  isAllDevices: boolean
}>()

const mode = ref<'simple' | 'detailed'>('detailed')

// --- Detailed Mode State ---
const containerWidth = ref(0)
const timelineEl = ref<HTMLElement | null>(null)
const viewStart = ref<number>(0)
const viewEnd = ref<number>(0)
const dayBounds = computed<Interval>(() => ({
  start: Date.parse(props.dayWindow.start),
  end: Date.parse(props.dayWindow.endExclusive),
}))

// --- Drag interaction (composable) ---
const {
  isDraggingTimeline,
  handleWheel,
  timelinePointerDown,
  minimapPointerDown,
} = useTimelineDrag(viewStart, viewEnd, timelineEl, dayBounds)

// Initialize view bounds
const initViewBounds = () => {
  const b = initialViewBounds(dayBounds.value, props.isToday, props.usageData, Date.now())
  viewStart.value = b.start
  viewEnd.value = b.end
}

// 首批数据就位或用户开始查看后，刷新不再重置视窗。
let viewEstablished = false
watch(() => `${props.dayWindow.start}/${props.dayWindow.endExclusive}/${props.dayWindow.timeZone}`, () => {
  viewEstablished = false
  initViewBounds()
}, { immediate: true })
watch(() => props.usageData, usage => {
  if (viewEstablished || !usage.length) return
  initViewBounds()
  viewEstablished = true
}, { immediate: true })

onMounted(() => {
  if (timelineEl.value) {
    containerWidth.value = timelineEl.value.clientWidth
    window.addEventListener('resize', handleResize)
  }
})

onUnmounted(() => {
  window.removeEventListener('resize', handleResize)
})

const handleResize = () => {
  if (timelineEl.value) containerWidth.value = timelineEl.value.clientWidth
}

// ========== 模型（纯逻辑在 timeline/timelineModel.ts，此处只做响应式接线与显示映射） ==========

const parsed = computed(() => parseUsage(props.usageData))

function rowsOf(usage: AppUsageResponse[]) {
  return buildRows(
    parseUsage(usage),
    { start: viewStart.value, end: viewEnd.value },
    props.dayWindow.timeZone,
  ).map(row => ({
    ...row,
    name: row.isAway ? '离开' : (props.appNameMap.get(row.appId) || `App ${row.appId}`),
  }))
}

const detailedRows = computed(() => rowsOf(props.usageData))

/**
 * 设备泳道：聚合视图下设备是最外层分组键（system 互斥不变量只在单设备内成立，
 * 跨设备并发是真事实，不能压进同一条轨）。当天无段的设备不出轨。
 */
const deviceLanes = computed(() => {
  if (!props.isAllDevices) return []
  const groups = groupByDevice(props.usageData)
  if (groups.size <= 1) return []   // 单台活跃 → 自然退化成普通单设备时间线
  const lanes: { deviceId: number; deviceName: string; rows: ReturnType<typeof rowsOf> }[] = []
  for (const [deviceId, usage] of groups) {
    const rows = rowsOf(usage as AppUsageResponse[])
    if (rows.length === 0) continue
    lanes.push({
      deviceId,
      deviceName: props.devices.find(d => d.id === deviceId)?.name ?? `设备 ${deviceId}`,
      rows,
    })
  }
  return lanes
})

const showLanes = computed(() => deviceLanes.value.length > 1)

// 泳道模式下每轨压到 5 行高（≈2 台 × 5 行仍在一屏内），单轨维持原高度。
const rowsMaxHeightClass = computed(() =>
  showLanes.value
    ? 'max-h-[200px] min-[900px]:max-h-[240px]'
    : 'max-h-[220px] min-[900px]:max-h-[320px] min-[1200px]:max-h-[400px]'
)

// Adaptive tick intervals based on available width
const ticks = computed(() => {
  const trackWidth = (containerWidth.value || 800) - 120
  const maxTicks = Math.max(2, Math.floor(trackWidth / 70))
  return niceTicks(viewStart.value, viewEnd.value, maxTicks, props.dayWindow.timeZone)
})

// ========== Minimap ==========

const simpleBins = computed(() =>
  buildDayHourBins(dayBounds.value, props.usageData, props.dayWindow.timeZone)
)

const simpleTicks = computed(() =>
  niceTicks(dayBounds.value.start, dayBounds.value.end, 5, props.dayWindow.timeZone)
)

const minimapRangeStyle = computed(() => {
  const day = dayBounds.value.end - dayBounds.value.start
  if (day <= 0) return { left: '0%', width: '0%' }
  const l = ((viewStart.value - dayBounds.value.start) / day) * 100
  const w = ((viewEnd.value - viewStart.value) / day) * 100
  return { left: `${Math.max(0, l)}%`, width: `${Math.min(100 - l, w)}%` }
})

const minimapActivities = computed(() => {
  return mergeActivityBursts(parsed.value).flatMap(interval => {
    const projected = projectInterval(interval, dayBounds.value)
    return projected
      ? [{ left: `${projected.left}%`, width: `${Math.max(0.2, projected.width)}%` }]
      : []
  })
})
</script>

<template>
  <DashboardCard>
    <div class="flex items-center justify-between">
      <h2 class="text-sm font-semibold text-foreground">活动时间线</h2>
      <div class="flex gap-0.5 rounded-full border border-glass-border bg-glass p-0.5 shadow-sm backdrop-blur-md">
        <button
          class="flex cursor-pointer items-center justify-center rounded-full p-1.5 transition-colors"
          :class="mode === 'simple' ? 'bg-primary text-primary-foreground shadow-sm' : 'text-muted-foreground hover:bg-accent hover:text-foreground'"
          @click="mode = 'simple'"
          title="24小时热力图"
        >
          <LayoutGrid :size="16" />
        </button>
        <button
          class="flex cursor-pointer items-center justify-center rounded-full p-1.5 transition-colors"
          :class="mode === 'detailed' ? 'bg-primary text-primary-foreground shadow-sm' : 'text-muted-foreground hover:bg-accent hover:text-foreground'"
          @click="mode = 'detailed'"
          title="详细时间线"
        >
          <AlignJustify :size="16" />
        </button>
      </div>
    </div>

    <!-- Simple Mode -->
    <div v-if="mode === 'simple'">
      <div class="mb-2 flex h-[30px] gap-1">
        <div
          v-for="bin in simpleBins"
          :key="bin.start"
          class="flex-1 rounded border transition-colors duration-300"
          :class="bin.active ? 'border-primary bg-primary' : 'border-border bg-card'"
          :title="bin.label"
        ></div>
      </div>
      <div class="relative h-4 font-mono text-xs text-muted-foreground">
        <span
          v-for="tick in simpleTicks"
          :key="tick.at"
          class="absolute -translate-x-1/2"
          :style="{ left: tick.percent + '%' }"
        >{{ tick.label }}</span>
      </div>
    </div>

    <!-- Detailed Mode -->
    <div v-else class="flex select-none flex-col gap-3" ref="timelineEl" @pointerdown.capture="viewEstablished = true" @wheel="viewEstablished = true; handleWheel($event)">
      <!-- Minimap -->
      <div class="relative h-6 overflow-hidden rounded border border-border bg-secondary">
        <div class="absolute inset-0">
          <div
            v-for="(burst, i) in minimapActivities"
            :key="i"
            class="absolute bottom-1.5 top-1.5 rounded-sm bg-accent-3 opacity-60"
            :style="{ left: burst.left, width: burst.width }"
          ></div>
        </div>
        <div
          class="absolute bottom-0 top-0 box-border cursor-grab touch-pan-y border border-primary bg-primary-soft active:cursor-grabbing"
          :style="minimapRangeStyle"
          @mousedown="minimapPointerDown($event, 'center')"
          @touchstart.prevent="minimapPointerDown($event, 'center')"
        >
          <div
            class="absolute bottom-0 top-0 left-0 w-2 cursor-ew-resize bg-primary"
            @mousedown.stop="minimapPointerDown($event, 'left')"
            @touchstart.stop.prevent="minimapPointerDown($event, 'left')"
          ></div>
          <div
            class="absolute bottom-0 top-0 right-0 w-2 cursor-ew-resize bg-primary"
            @mousedown.stop="minimapPointerDown($event, 'right')"
            @touchstart.stop.prevent="minimapPointerDown($event, 'right')"
          ></div>
        </div>
      </div>

      <!-- Main Timeline -->
      <div
        class="overflow-hidden rounded-md border border-border bg-secondary touch-pan-y"
        :class="isDraggingTimeline ? 'cursor-grabbing' : 'cursor-grab'"
        @mousedown="timelinePointerDown($event)"
        @touchstart="timelinePointerDown($event)"
      >
        <div class="flex h-6 border-b border-border bg-muted">
          <div class="w-[80px] shrink-0 border-r border-border min-[640px]:w-[120px]"></div>
          <div class="relative flex-1">
            <div
              class="pointer-events-none absolute bottom-0 top-0 flex -translate-x-1/2 flex-col items-center"
              v-for="t in ticks"
              :key="t.at"
              :style="{ left: t.percent + '%' }"
            >
              <span class="mt-0.5 font-mono text-[0.65rem] text-muted-foreground">{{ t.label }}</span>
              <div class="absolute top-6 -bottom-[500px] z-0 w-px bg-border"></div>
            </div>
          </div>
        </div>

        <!-- 设备泳道模式：每台设备一组，组内是该设备的应用轨 -->
        <div
          v-if="showLanes"
          class="timeline-rows relative z-[1] overflow-y-auto"
          :class="rowsMaxHeightClass"
        >
          <div v-for="lane in deviceLanes" :key="lane.deviceId">
            <div class="sticky top-0 z-[2] flex h-6 items-center gap-2 border-b border-border bg-muted/95 px-2 backdrop-blur-sm">
              <span class="truncate text-[0.7rem] font-semibold uppercase tracking-[0.06em] text-muted-foreground">
                {{ lane.deviceName }}
              </span>
            </div>
            <div
              v-for="row in lane.rows"
              :key="row.appId"
              class="flex h-9 border-b border-border last:border-b-0"
            >
              <div class="row-header z-[2] flex w-[80px] shrink-0 items-center gap-2 border-r border-border bg-muted px-2 min-[640px]:w-[120px]">
                <AppIcon v-if="!row.isAway" :username="username" :app-id="row.appId" class="h-5 w-5 rounded object-contain" />
                <span v-else class="flex h-5 w-5 shrink-0 items-center justify-center text-muted-foreground">💤</span>
                <span class="flex-1 truncate text-[0.75rem]" :class="row.isAway ? 'text-muted-foreground' : 'text-foreground'" :title="row.name">{{ row.name }}</span>
              </div>
              <div class="relative flex-1">
                <div
                  v-for="(bar, idx) in row.bars"
                  :key="idx"
                  class="absolute top-2 h-5 cursor-pointer rounded-sm opacity-80 hover:z-[3] hover:opacity-100"
                  :class="row.isAway ? 'bg-muted-foreground/40' : 'bg-primary'"
                  :style="{ left: bar.left + '%', width: bar.width + '%' }"
                  :title="bar.label"
                ></div>
              </div>
            </div>
          </div>
        </div>

        <TransitionGroup
          v-else
          name="row-list"
          tag="div"
          class="timeline-rows relative z-[1] overflow-y-auto"
          :class="rowsMaxHeightClass"
        >
          <div
            v-if="detailedRows.length === 0"
            key="empty"
            class="p-8 text-center text-[0.8rem] text-muted-foreground"
          >
            {{ loading ? '正在读取活动…' : '当前范围无活动，可拖动上方缩略图查看其他时段' }}
          </div>
          <div
            v-for="row in detailedRows"
            :key="row.appId"
            class="flex h-10 border-b border-border last:border-b-0"
          >
            <!-- row-header / timeline-rows 是 useTimelineDrag 的功能性选择器锚点，不是样式 -->
            <div class="row-header z-[2] flex w-[80px] shrink-0 items-center gap-2 border-r border-border bg-muted px-2 min-[640px]:w-[120px]">
              <AppIcon v-if="!row.isAway" :username="username" :app-id="row.appId" class="h-5 w-5 rounded object-contain" />
              <span v-else class="flex h-5 w-5 shrink-0 items-center justify-center text-muted-foreground">💤</span>
              <span class="flex-1 truncate text-[0.75rem]" :class="row.isAway ? 'text-muted-foreground' : 'text-foreground'" :title="row.name">{{ row.name }}</span>
            </div>
            <div class="relative flex-1">
              <div
                v-for="(bar, idx) in row.bars"
                :key="idx"
                class="absolute top-2.5 h-5 cursor-pointer rounded-sm opacity-80 hover:z-[3] hover:opacity-100"
                :class="row.isAway ? 'bg-muted-foreground/40' : 'bg-primary'"
                :style="{ left: bar.left + '%', width: bar.width + '%' }"
                :title="bar.label"
              ></div>
            </div>
          </div>
        </TransitionGroup>
      </div>
    </div>
  </DashboardCard>
</template>

<style scoped>
/* TransitionGroup 行重排动画依赖具名 class，保留为 scoped CSS */
.row-list-move {
  transition: transform 0.3s ease;
}
.row-list-enter-active {
  transition: opacity 0.2s ease;
}
.row-list-leave-active {
  transition: opacity 0.15s ease;
  position: absolute;
  width: 100%;
}
.row-list-enter-from,
.row-list-leave-to {
  opacity: 0;
}
</style>
