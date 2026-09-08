<script setup lang="ts">
import { computed } from 'vue'
import { getAppLabel, isAwayApp } from '../appLabels'
import type { DevicePresence } from '../composables/useDeviceStatus'
import AppIcon from './AppIcon.vue'
import DashboardCard from './DashboardCard.vue'

const props = defineProps<{
  username: string
  isToday: boolean
  presences: DevicePresence[]
}>()

const onlinePresences = computed(() => props.presences.filter(p => p.isOnline))
</script>

<template>
  <DashboardCard v-if="isToday && onlinePresences.length > 0">
    <h2 class="text-sm font-semibold text-foreground">当前使用</h2>

    <!-- 设备筛选由在场域负责，单台和多台使用同一个列表。 -->
    <div
      v-for="p in onlinePresences"
      :key="p.deviceId"
      class="flex items-center gap-3 py-1"
    >
      <span class="status-dot" :class="{ alive: p.isOnline }"></span>
      <AppIcon
        v-if="p.currentAppId && !isAwayApp(p.currentAppKey, p.currentApp)"
        :username="username"
        :app-id="p.currentAppId"
        class="h-6 w-6 shrink-0 object-contain"
      />
      <div class="flex min-w-0 flex-col gap-0.5">
        <span
          class="truncate text-[1rem]"
          :class="p.currentApp && !isAwayApp(p.currentAppKey, p.currentApp)
            ? 'font-semibold'
            : 'font-normal text-muted-foreground'"
        >
          {{ isAwayApp(p.currentAppKey, p.currentApp) ? '离开中' : (p.currentApp ?? '无前台应用') }}
        </span>
        <span v-if="p.currentApp && getAppLabel(p.currentAppKey ?? p.currentApp)" class="text-[0.7rem] text-muted-foreground">
          {{ getAppLabel(p.currentAppKey ?? p.currentApp) }}
        </span>
        <span class="truncate text-[0.75rem] text-muted-foreground">
          {{ p.deviceName }}
        </span>
      </div>
    </div>
  </DashboardCard>
</template>
