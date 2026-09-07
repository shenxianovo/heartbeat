<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { ArrowLeft, RefreshCw } from 'lucide-vue-next'
import { fetchManagedCollectors, fetchManagedOperations, cancelManagedOperation, type ManagedCollectorStatus, type HostManagementOperation } from '../api/index'
import ManagedCollectorCard from '../components/ManagedCollectorCard.vue'
import { Button } from '@/components/ui/button'

const collectors = ref<ManagedCollectorStatus[]>([])
const operations = ref<HostManagementOperation[]>([])
const loading = ref(true)
const refreshing = ref(false)
const error = ref('')
const operationError = ref('')
const lifetime = new AbortController()
let catalogRefresh: Promise<void> | undefined
let operationRefresh: Promise<void> | undefined
let timer: ReturnType<typeof setTimeout> | undefined

const operationNames = { Install: '安装', Retry: '重试', Uninstall: '卸载', SubmitAuthorization: '授权提交' }
function operationStatus(operation: HostManagementOperation): string {
  const phase = { Pending: '等待中', Running: '中', Committing: '收尾中', Succeeded: '完成', Cancelled: '已取消', Failed: '失败' }
  return operationNames[operation.kind] + phase[operation.phase]
}

function refreshCatalog(): Promise<void> {
  return catalogRefresh ??= fetchManagedCollectors(lifetime.signal)
    .then(next => { collectors.value = next; error.value = '' })
    .catch(() => { if (!lifetime.signal.aborted) error.value = '无法连接 Hub 或 Collector Catalog，请确认部署状态' })
    .finally(() => { catalogRefresh = undefined; loading.value = false })
}

function refresh(): Promise<void> {
  if (timer) clearTimeout(timer)
  // Operation results must remain readable while a catalog request waits for a running mutation.
  void refreshCatalog()
  return operationRefresh ??= fetchManagedOperations(lifetime.signal)
    .then(next => { operations.value = next; operationError.value = '' })
    .catch(() => { if (!lifetime.signal.aborted) operationError.value = '无法查询 Hub 操作结果' })
    .finally(() => {
      operationRefresh = undefined
      refreshing.value = false
      if (!lifetime.signal.aborted) timer = setTimeout(() => void refresh(), 5_000)
    })
}

async function cancel(operation: HostManagementOperation) {
  try {
    const result = await cancelManagedOperation(operation.operationId)
    await refresh()
    if (result === 'NotCancellable') operationError.value = '操作正在收尾，已无法取消。'
  } catch { operationError.value = '取消失败，请刷新后重试。' }
}

onMounted(() => void refresh())
onBeforeUnmount(() => {
  lifetime.abort()
  if (timer) clearTimeout(timer)
})
</script>

<template>
  <div class="mx-auto w-[min(100%,800px)] px-4 py-8 sm:px-8">
    <header class="mb-8 flex flex-wrap items-start justify-between gap-4">
      <div>
        <h1 class="text-2xl font-bold">Hub 管理</h1>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          从 Collector Catalog 安装采集器、查看运行状态，并完成采集器请求的授权。
        </p>
      </div>
      <div class="flex gap-2">
        <Button variant="glass" size="sm" :disabled="refreshing" @click="refreshing = true; refresh()">
          <RefreshCw />
          刷新
        </Button>
        <Button variant="glass" size="sm" as-child>
          <router-link to="/settings">
            <ArrowLeft />
            返回设置
          </router-link>
        </Button>
      </div>
    </header>

    <div v-if="error" class="mb-4 rounded-lg border border-red-500/30 bg-red-500/10 px-4 py-3 text-sm text-red-200">
      {{ error }}
    </div>
    <div v-if="operationError" class="mb-4 text-sm text-red-300">{{ operationError }}</div>
    <div v-for="operation in operations" :key="operation.operationId" class="mb-3 flex items-center justify-between gap-3 rounded-lg border border-border/60 px-4 py-3 text-sm">
      <div>
        {{ collectors.find(collector => collector.packageId === operation.packageId)?.displayName ?? '采集器' }}：{{ operationStatus(operation) }}
        <p v-if="operation.failure" class="mt-1 text-red-300">{{ operation.failure }}</p>
      </div>
      <Button v-if="!operation.isTerminal && operation.phase !== 'Committing'" variant="glass" size="sm" @click="cancel(operation)">取消</Button>
    </div>
    <p v-if="loading" class="text-sm text-muted-foreground">加载 Collector Catalog…</p>
    <div v-else-if="collectors.length" class="flex flex-col gap-4">
      <ManagedCollectorCard
        v-for="collector in collectors"
        :key="collector.packageId"
        :collector="collector"
        @changed="refresh"
        :operation="operations.find(operation => operation.packageId === collector.packageId)"
      />
    </div>
    <div v-else-if="!error" class="rounded-lg border border-border/60 bg-card/60 px-5 py-8 text-center text-sm text-muted-foreground">
      Catalog 暂无适用于这个 Hub 的 Collector。
    </div>
  </div>
</template>
