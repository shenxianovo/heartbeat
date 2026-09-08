<script setup lang="ts">
import { formatDuration } from '../composables/useHeartbeat'
import AppIcon from './AppIcon.vue'
import DashboardCard from './DashboardCard.vue'

defineProps<{
  loading?: boolean
  username: string
  appSummaries: { appId: number; appName: string; totalSeconds: number }[]
  maxSeconds: number
  provisionalAppIds: Set<number>
}>()

const emit = defineEmits<{ select: [app: { appId: number; appName: string; totalSeconds: number }] }>()
</script>

<template>
  <DashboardCard>
    <h2 class="text-sm font-semibold text-foreground">当日应用排行</h2>

    <div
      v-if="appSummaries.length"
      class="flex max-h-[200px] flex-col gap-3 overflow-y-auto pr-1 min-[900px]:max-h-[280px] min-[1200px]:max-h-[340px]"
    >
      <button
        type="button"
        aria-haspopup="dialog"
        :aria-label="`查看 ${app.appName} 的活动详情`"
        v-for="(app, i) in appSummaries"
        :key="app.appId"
        class="flex cursor-pointer flex-col gap-1 rounded-md text-left transition-colors hover:bg-accent/50 focus-visible:outline-2 focus-visible:outline-primary"
        @click="emit('select', app)"
      >
        <div class="flex items-center gap-2 text-[0.85rem]">
          <span class="w-6 text-center text-xs font-semibold text-muted-foreground">{{ i + 1 }}</span>
          <AppIcon
            :username="username"
            :app-id="app.appId"
            class="h-[18px] w-[18px] rounded object-contain"
          />
          <span class="flex-1 truncate">{{ app.appName }}</span>
          <span
            v-if="provisionalAppIds.has(app.appId)"
            class="rounded-full border border-amber-400/30 bg-amber-400/10 px-1.5 py-0.5 text-[0.62rem] text-amber-200"
            title="此产品仍是 provisional，等待部署管理员归类"
          >待归类</span>
          <span class="font-mono text-[0.8rem] text-muted-foreground">{{ formatDuration(app.totalSeconds) }}</span>
          <span aria-hidden="true" class="text-muted-foreground">›</span>
        </div>
        <div class="ml-8 h-1 overflow-hidden rounded-sm bg-secondary">
          <div
            class="h-full rounded-sm bg-primary"
            :style="{ width: `${(app.totalSeconds / maxSeconds) * 100}%` }"
          ></div>
        </div>
      </button>
    </div>

    <div v-else class="py-8 text-center text-[0.9rem] text-muted-foreground">{{ loading ? '正在读取排行…' : '当日暂无应用记录' }}</div>
  </DashboardCard>
</template>
