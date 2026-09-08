<script setup lang="ts">
import { computed } from 'vue'
import type { IInputCountsResponse } from '../api/client'
import DashboardCard from './DashboardCard.vue'

const props = defineProps<{ counts: IInputCountsResponse | null; loading: boolean; failed: boolean }>()
defineEmits<{ retry: [] }>()
const metrics = computed(() => [
  { label: '左键', count: props.counts?.mouseLeft },
  { label: '右键', count: props.counts?.mouseRight },
  { label: '中键', count: props.counts?.mouseMiddle },
  { label: '向上滚动', count: props.counts?.scrollUp },
  { label: '向下滚动', count: props.counts?.scrollDown },
])
const intensity = (index: number) => 0.15 + 0.85 * Math.sqrt(
  (metrics.value[index].count ?? 0) / Math.max(1, ...metrics.value.slice(0, 3).map(m => m.count ?? 0)))
</script>

<template>
  <DashboardCard :aria-busy="loading">
    <h2 class="text-sm font-semibold">鼠标活动</h2>
    <div v-if="failed" role="alert" class="flex items-center justify-between gap-2 text-sm text-destructive">
      <span>鼠标统计读取失败{{ counts ? '，暂时显示上次结果' : '' }}</span>
      <button type="button" class="glass-control px-3 py-1" :disabled="loading" @click="$emit('retry')">重试</button>
    </div>
    <p v-else-if="loading && !counts" class="text-sm text-muted-foreground">正在读取鼠标统计…</p>
    <div v-if="counts" class="flex items-center gap-5">
      <svg viewBox="0 0 100 140" class="h-32 w-20 shrink-0" aria-hidden="true">
        <rect x="10" y="5" width="80" height="130" rx="40" fill="var(--secondary)" stroke="var(--border)" />
        <path d="M49 6C27 6 11 22 11 44V60H49Z" fill="var(--primary)" :opacity="intensity(0)" />
        <path d="M51 6C73 6 89 22 89 44V60H51Z" fill="var(--primary)" :opacity="intensity(1)" />
        <rect x="43" y="24" width="14" height="28" rx="7" fill="var(--primary-dark)" :opacity="intensity(2)" />
      </svg>
      <dl class="grid min-w-0 flex-1 grid-cols-2 gap-x-4 gap-y-3 min-[640px]:grid-cols-3">
        <div v-for="metric in metrics" :key="metric.label">
          <dt class="text-xs text-muted-foreground">{{ metric.label }}</dt>
          <dd class="font-mono text-lg font-semibold">{{ metric.count?.toLocaleString() ?? '—' }}<span class="ml-1 text-xs font-normal text-muted-foreground">次</span></dd>
        </div>
      </dl>
    </div>
  </DashboardCard>
</template>
