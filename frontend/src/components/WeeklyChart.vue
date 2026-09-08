<script setup lang="ts">
import { ref, computed } from 'vue'
import { formatDuration } from '../composables/useHeartbeat'
import AppIcon from './AppIcon.vue'
import DashboardCard from './DashboardCard.vue'

const props = defineProps<{
  loading?: boolean
  username: string
  weeklyAppSummaries: { appId: number; appName: string; totalSeconds: number }[]
  weeklyTotalSeconds: number
}>()

const DONUT_MAX_ITEMS = 10
const CIRCUMFERENCE = 2 * Math.PI * 70
const CHART_COLORS = [
  'var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)',
  'var(--accent-3)', 'var(--primary-dark)', 'var(--accent-2)', 'var(--destructive)', 'var(--accent-1)',
]

const hoveredSegment = ref<number | null>(null)

const donutSegments = computed(() => {
  const total = props.weeklyTotalSeconds
  if (total === 0) return []

  const items = props.weeklyAppSummaries.slice(0, DONUT_MAX_ITEMS)
  const otherSeconds = props.weeklyAppSummaries
    .slice(DONUT_MAX_ITEMS)
    .reduce((s, a) => s + a.totalSeconds, 0)

  const all = otherSeconds > 0
    ? [...items, { appId: 0, appName: '其他', totalSeconds: otherSeconds }]
    : items

  let offset = 0
  return all.map((app, i) => {
    const fraction = app.totalSeconds / total
    const length = fraction * CIRCUMFERENCE
    const seg = {
      appId: app.appId,
      appName: app.appName,
      totalSeconds: app.totalSeconds,
      percentage: (fraction * 100).toFixed(1),
      color: CHART_COLORS[i % CHART_COLORS.length],
      length,
      offset,
    }
    offset += length
    return seg
  })
})
</script>

<template>
  <DashboardCard>
    <h2 class="text-sm font-semibold text-foreground">所在周应用使用</h2>

    <div
      v-if="donutSegments.length"
      class="flex items-center gap-8 max-[640px]:flex-col min-[900px]:flex-col min-[900px]:items-center"
    >
      <!-- Donut -->
      <div class="relative h-[200px] w-[200px] shrink-0 min-[900px]:h-[170px] min-[900px]:w-[170px] min-[1200px]:h-[190px] min-[1200px]:w-[190px]">
        <svg viewBox="0 0 200 200" class="h-full w-full overflow-visible">
          <circle cx="100" cy="100" r="70" fill="none" stroke="var(--secondary)" stroke-width="30" />
          <circle
            v-for="(seg, i) in donutSegments"
            :key="i"
            cx="100" cy="100" r="70"
            fill="none"
            :stroke="seg.color"
            :stroke-width="hoveredSegment === i ? 35 : 30"
            :stroke-dasharray="`${seg.length} ${CIRCUMFERENCE - seg.length}`"
            :stroke-dashoffset="`${-seg.offset}`"
            transform="rotate(-90 100 100)"
            class="donut-segment"
            @mouseenter="hoveredSegment = i"
            @mouseleave="hoveredSegment = null"
          />
        </svg>
        <div class="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
          <template v-if="hoveredSegment !== null">
            <AppIcon
              :username="username"
              :app-id="donutSegments[hoveredSegment].appId"
              class="mb-1 h-6 w-6 object-contain"
            />
            <span class="max-w-[100px] truncate text-center text-[0.8rem] font-semibold text-foreground">
              {{ donutSegments[hoveredSegment].appName }}
            </span>
            <span class="font-mono text-base font-bold text-foreground">{{ formatDuration(donutSegments[hoveredSegment].totalSeconds) }}</span>
            <span class="text-xs text-muted-foreground">{{ donutSegments[hoveredSegment].percentage }}%</span>
          </template>
          <template v-else>
            <span class="text-[0.8rem] font-semibold text-foreground">该周总计</span>
            <span class="font-mono text-base font-bold text-foreground">{{ formatDuration(weeklyTotalSeconds) }}</span>
          </template>
        </div>
      </div>

      <!-- Legend -->
      <div class="flex max-h-[200px] flex-1 flex-col gap-2 overflow-y-auto pr-1 min-[900px]:max-h-[170px] min-[900px]:w-full min-[1200px]:max-h-[200px]">
        <div
          v-for="(seg, i) in donutSegments"
          :key="seg.appName"
          class="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1 text-[0.8rem] transition-[background,opacity] duration-200 hover:bg-accent"
          :class="{ 'opacity-30': hoveredSegment !== null && hoveredSegment !== i }"
          @mouseenter="hoveredSegment = i"
          @mouseleave="hoveredSegment = null"
        >
          <span class="h-2 w-2 shrink-0 rounded-full" :style="{ background: seg.color }"></span>
          <AppIcon
            :username="username"
            :app-id="seg.appId"
            class="h-4 w-4 shrink-0 object-contain"
          />
          <span class="flex-1 truncate">{{ seg.appName }}</span>
          <span class="font-mono text-muted-foreground">{{ formatDuration(seg.totalSeconds) }}</span>
          <span class="w-12 text-right text-muted-foreground">{{ seg.percentage }}%</span>
        </div>
      </div>
    </div>

    <div v-else class="py-8 text-center text-[0.9rem] text-muted-foreground">{{ loading ? '正在读取周统计…' : '该周暂无应用记录' }}</div>
  </DashboardCard>
</template>

<style scoped>
/* SVG 描边的尺寸过渡用 utility 不直观，保留少量 scoped 规则 */
.donut-segment {
  transition: stroke-width 0.2s ease, opacity 0.2s;
  cursor: pointer;
}
</style>
