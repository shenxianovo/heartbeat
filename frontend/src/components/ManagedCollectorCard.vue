<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import {
  installManagedCollector,
  retryManagedCollector,
  submitCollectorAuthorization,
  uninstallManagedCollector,
  type ManagedCollectorStatus,
  type HostManagementOperation,
} from '../api/index'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'

const props = defineProps<{ collector: ManagedCollectorStatus; operation?: HostManagementOperation }>()
const emit = defineEmits<{ changed: [packageId: string] }>()
const values = reactive<Record<string, string>>({})
const error = ref('')
const submitting = ref(false)
const busy = computed(() => submitting.value || (!!props.operation && !props.operation.isTerminal))
const submitted = ref(false)

watch(
  () => props.collector.authorization?.interactionId,
  () => {
    for (const key of Object.keys(values)) delete values[key]
    for (const field of props.collector.authorization?.fields ?? []) values[field.name] = ''
    error.value = ''
    submitted.value = false
  },
  { immediate: true },
)

watch(() => props.operation, operation => {
  if (operation?.kind === 'SubmitAuthorization' && ['Failed', 'Cancelled'].includes(operation.phase)) submitted.value = false
})

const statusTitle = computed(() => {
  if (!props.collector.isInstalled) return '可安装'
  if (props.collector.authorization) return '需要登录'
  if (props.collector.phase === 'Ready') return '运行中'
  if (props.collector.phase === 'Failed') return '运行异常'
  return '正在启动'
})

const statusDescription = computed(() => {
  if (!props.collector.isInstalled) return `最新版本 ${props.collector.latestVersion ?? '未知'}`
  if (props.collector.authorization) return props.collector.authorization.message ?? '完成授权后采集器会继续启动。'
  if (props.collector.currentActivity?.title) return `当前状态：${props.collector.currentActivity.title}`
  if (props.collector.statusDetail) return props.collector.statusDetail
  if (props.collector.phase === 'Ready') return '采集器已就绪，当前没有可展示的状态。'
  return 'Hub 正在启动这个采集器。'
})

async function install() {
  if (busy.value) return
  submitting.value = true
  error.value = ''
  try {
    await installManagedCollector(props.collector.packageId)
    emit('changed', props.collector.packageId)
  } catch {
    error.value = '安装失败，请稍后重试或检查 Hub 日志'
  } finally {
    submitting.value = false
  }
}

async function uninstall() {
  if (busy.value) return
  if (!window.confirm(`卸载 ${props.collector.displayName}？它的登录信息和本地数据也会被删除。`)) return
  submitting.value = true
  error.value = ''
  try {
    await uninstallManagedCollector(props.collector.packageId)
    emit('changed', props.collector.packageId)
  } catch {
    error.value = '卸载失败，请稍后重试或检查 Hub 日志'
  } finally {
    submitting.value = false
  }
}

async function retry() {
  if (busy.value) return
  submitting.value = true
  error.value = ''
  try {
    await retryManagedCollector(props.collector.packageId)
    emit('changed', props.collector.packageId)
  } catch {
    error.value = '重试失败，请检查 Hub 日志'
  } finally {
    submitting.value = false
  }
}

async function submitAuthorization() {
  const authorization = props.collector.authorization
  if (!authorization || busy.value || submitted.value) return
  submitting.value = true
  error.value = ''
  try {
    if (!props.collector.collectorInstanceId) throw new Error('Collector Instance is not initialized.')
    await submitCollectorAuthorization(
      props.collector.collectorInstanceId,
      authorization.interactionId,
      { ...values },
    )
    submitted.value = true
    emit('changed', props.collector.packageId)
  } catch {
    error.value = '提交失败，请确认信息后重试'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <Card class="gap-4 border-border/60 bg-card/80 px-5 py-5 backdrop-blur-sm">
    <div class="flex items-start justify-between gap-4">
      <div class="min-w-0">
        <div class="truncate text-base font-semibold">{{ collector.displayName }}</div>
        <p class="mt-1 text-sm leading-relaxed text-muted-foreground">{{ collector.summary }}</p>
      </div>
      <div class="flex shrink-0 items-center gap-2 text-sm text-muted-foreground">
        <span class="status-dot" :class="{ alive: collector.phase === 'Ready' && !collector.authorization }"></span>
        {{ statusTitle }}
      </div>
    </div>

    <div class="flex flex-wrap items-center justify-between gap-3">
      <p class="text-sm leading-relaxed text-muted-foreground">{{ statusDescription }}</p>
      <Button
        v-if="!collector.isInstalled"
        variant="glassPrimary"
        size="sm"
        :disabled="busy"
        @click="install"
      >
        {{ busy ? '安装中…' : '安装' }}
      </Button>
      <div v-else class="flex gap-2">
        <Button v-if="collector.phase === 'Failed'" variant="glassPrimary" size="sm" :disabled="busy" @click="retry">
          重试
        </Button>
        <Button variant="glass" size="sm" :disabled="busy" @click="uninstall">
          {{ busy ? '处理中…' : '卸载' }}
        </Button>
      </div>
    </div>

    <form
      v-if="collector.authorization"
      class="flex flex-col gap-3 rounded-lg border border-border/50 bg-background/30 p-4"
      @submit.prevent="submitAuthorization"
    >
      <div>
        <div class="text-sm font-semibold">{{ collector.authorization.title }}</div>
        <div class="mt-1 text-xs text-muted-foreground">授权信息只会发送给你的 Hub 与这个采集器。</div>
      </div>
      <label
        v-for="field in collector.authorization.fields"
        :key="field.name"
        class="flex flex-col gap-1.5 text-xs text-muted-foreground"
      >
        {{ field.label }}
        <input
          v-model="values[field.name]"
          :type="field.isSecret ? 'password' : 'text'"
          :inputmode="field.inputMode ?? undefined"
          autocomplete="off"
          required
          class="glass-control px-3 py-2 text-sm text-foreground"
        />
      </label>
      <div v-if="submitted" class="text-xs text-primary">已提交，等待采集器响应…</div>
      <div class="flex justify-end">
        <Button variant="glassPrimary" size="sm" type="submit" :disabled="busy || submitted">
          {{ busy ? '提交中…' : (collector.authorization.fields.length ? '继续' : '确认') }}
        </Button>
      </div>
    </form>

    <div v-if="error" class="text-xs text-red-300">{{ error }}</div>
  </Card>
</template>
