<script setup lang="ts">
import { ref, watch } from 'vue'
import {
  fetchDailyQuestions, fetchStrands, proposeFromQuestion, commitChangeSet, muteMatcher,
  toApiError, changeSetErrorOf, knowledgeErrorOf, KnowledgeOperationDto,
  type IAskingQuestionResponse, type IKnowledgeProposalResponse, type IStrandResponse,
} from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'
import {
  toReviewItems, selectedOps, canCommit, dateOnlyLabel,
  describeMatcher, formatTimeRange, isRecurrence,
  interpretProposeError, interpretCommitError, commitSummary,
  type ReviewItem,
} from '../teaching/teachingFlow'
import ProposalReview from './ProposalReview.vue'
import DashboardCard from './DashboardCard.vue'
import type { CalendarContext } from '../calendar/localCalendarWindow'

/**
 * 主动教学两阶段面板（ADR-031 §6，issue 05）：owner-only。
 * 证据卡（真实活动簇的时段 + 跨 Source 观察）→ 用户自然语言解释 → LLM 整理成可逐项
 * 编辑/取消的 KnowledgeChangeSet → 显式确认 → 事务提交。提案阶段零写入，只有最终
 * 确认才调 commit。跳过纯客户端；静音按确认后的 Mute 语义提交（cluster 静音指纹，
 * recurrence 解决探针），不隐藏原始 Observation。
 */
const props = defineProps<{ calendarContext: CalendarContext }>()

type Stage = 'evidence' | 'proposing' | 'review' | 'committing' | 'done'

interface TeachingCard {
  q: IAskingQuestionResponse
  stage: Stage
  answer: string
  proposal: IKnowledgeProposalResponse | null
  items: ReviewItem[]
  /** 当前阶段的人话错误；提交失败不清空用户编辑。 */
  error: string | null
  /** 证据已过期（服务端 404）：只能刷新问题列表，不能带着旧证据继续。 */
  expired: boolean
  /** commit 验证失败定位到的操作。 */
  failedOpId: string | null
  /** 409 并发冲突：提供重新整理提案（刷新版本）路径，不静默覆盖。 */
  conflict: boolean
  muteConfirm: boolean
  busy: boolean
  summary: string[]
}

const cards = ref<TeachingCard[]>([])
const readingLabels = ref<Record<string, string>>({})
const strands = ref<IStrandResponse[]>([])

async function load() {
  const expectedIdentity = props.calendarContext.correlationIdentity
  // refresh generation 已切换时，旧问题不能继续接受回答；新读取完成前先收起旧证据。
  cards.value = []
  readingLabels.value = {}
  try {
    const res = await fetchDailyQuestions({ window: props.calendarContext.day })
    if (props.calendarContext.correlationIdentity !== expectedIdentity) return
    readingLabels.value = res.readingLabels ?? {}
    cards.value = (res.questions ?? []).map(q => ({
      q,
      stage: 'evidence' as Stage,
      answer: '',
      proposal: null,
      items: [],
      error: null,
      expired: false,
      failedOpId: null,
      conflict: false,
      muteConfirm: false,
      busy: false,
      summary: [],
    }))
  } catch {
    // 提问是可选增强，取数失败静默不打扰；旧 generation 的失败也不能清掉新列表。
    if (props.calendarContext.correlationIdentity === expectedIdentity) cards.value = []
  }
}

watch(() => props.calendarContext.correlationIdentity, load, { immediate: true })

function remove(c: TeachingCard) {
  cards.value = cards.value.filter(x => x !== c)
}

// ===== Stage 1 → 2：自然语言回答换提案（零写入） =====

async function propose(c: TeachingCard) {
  if (!c.answer.trim() || !c.q.id || !c.q.windowKey) return
  const context = props.calendarContext
  const expectedIdentity = context.correlationIdentity
  c.stage = 'proposing'
  c.error = null
  c.conflict = false
  c.failedOpId = null
  try {
    const [proposal] = await Promise.all([
      proposeFromQuestion(c.q.id, {
        window: context.day,
        windowKey: c.q.windowKey,
        answer: c.answer,
      }),
      loadStrands(expectedIdentity),
    ])
    if (
      props.calendarContext.correlationIdentity !== expectedIdentity
      || !cards.value.includes(c)
    ) return
    c.proposal = proposal
    c.items = toReviewItems(proposal)
    // 提案可能引入证据卡之外的读数（如 LLM 引用了别的 Source 指纹）:标签词典做并集
    readingLabels.value = { ...readingLabels.value, ...(proposal.readingLabels ?? {}) }
    c.stage = 'review'
  } catch (e) {
    c.stage = 'evidence' // 回答保留，可修改后重试
    const failure = interpretProposeError(toApiError(e), knowledgeErrorOf(e)?.code)
    c.expired = failure.expired
    c.error = failure.message
  }
}

/** 已有 Strand 树（path/日期/版本）：review 里"选择已有节点"的消歧数据源。失败不挡 review。 */
async function loadStrands(expectedIdentity = props.calendarContext.correlationIdentity) {
  try {
    const next = await fetchStrands()
    if (props.calendarContext.correlationIdentity === expectedIdentity) strands.value = next
  } catch {
    // 列表加载失败时,已有节点只能按 Id 展示;不阻塞主流程
  }
}

// ===== Stage 2 → commit：只有显式确认才写入 =====

async function commit(c: TeachingCard) {
  const ops = selectedOps(c.items)
  if (ops.length === 0) return
  c.stage = 'committing'
  c.error = null
  c.failedOpId = null
  c.conflict = false
  try {
    const res = await commitChangeSet(ops)
    c.summary = commitSummary(res.results ?? [])
    c.stage = 'done'
  } catch (e) {
    c.stage = 'review' // 用户编辑内容原样保留
    const failure = interpretCommitError(changeSetErrorOf(e), toApiError(e))
    c.failedOpId = failure.failedOpId
    c.conflict = failure.conflict
    c.error = failure.message
  }
}

/** 并发冲突出口：基于最新知识重新整理提案（版本重新盖章），再走一遍审阅。 */
async function reproposeAfterConflict(c: TeachingCard) {
  await propose(c)
}

// ===== 静音：按确认后的 Mute 语义提交 =====

async function confirmMute(c: TeachingCard) {
  c.busy = true
  c.error = null
  try {
    if (isRecurrence(c.q) && c.q.probeId) {
      // recurrence 的"别再问"= 解决探针为 muted（静音指纹不会停掉活跃 Probe）
      await commitChangeSet([KnowledgeOperationDto.fromJS({
        opId: 'op1', type: 'resolveProbe', resolveProbe: { probeId: c.q.probeId, resolution: 'muted' },
      })])
    } else if (c.q.matcher) {
      await muteMatcher(c.q.matcher)
    }
    remove(c)
  } catch {
    c.busy = false
    c.muteConfirm = false
    c.error = '静音失败，请重试'
  }
}

/** 回到第一阶段修改回答：当前提案作废（重新整理会全量覆盖），编辑不保留。 */
function backToAnswer(c: TeachingCard) {
  c.stage = 'evidence'
  c.error = null
  c.conflict = false
  c.failedOpId = null
}

</script>

<template>
  <DashboardCard v-if="cards.length > 0">
    <h2 class="text-sm font-semibold text-foreground">
      认识一下 · {{ cards.length }} 个说不清的活动
    </h2>

    <div
      v-for="c in cards"
      :key="c.q.id ?? ''"
      class="flex flex-col gap-3 rounded-lg border border-border/50 bg-background/40 p-4"
    >
      <!-- ===== 证据卡:系统观察到的活动,不是已确定的归属 ===== -->
      <p class="text-[0.92rem]">{{ c.q.question }}</p>

      <div v-if="isRecurrence(c.q)" class="rounded-md border border-border/40 bg-background/50 px-3 py-2 text-[0.8rem] text-muted-foreground">
        上次记录：「{{ c.q.episodeText }}」（{{ c.q.episodeDate ? dateOnlyLabel(c.q.episodeDate) : '' }}）
      </div>

      <div class="flex flex-col gap-1.5">
        <p class="text-[0.72rem] text-muted-foreground/60">
          系统观察到的活动<template v-if="formatTimeRange(c.q.approximateStart, c.q.approximateEnd)">（{{ formatTimeRange(c.q.approximateStart, c.q.approximateEnd) }} 前后）</template>——归属由你决定：
        </p>
        <ul class="flex flex-col gap-0.5">
          <li
            v-for="(o, i) in c.q.observations ?? []"
            :key="i"
            class="flex items-baseline gap-2 text-[0.8rem]"
            :class="o.matchesFingerprint ? 'text-foreground' : 'text-muted-foreground/70'"
          >
            <span class="shrink-0 font-mono text-[0.7rem] text-muted-foreground/50">{{ o.source }}</span>
            <span class="min-w-0 truncate">{{ o.value }}<template v-if="o.detail"> · {{ o.detail }}</template></span>
            <span class="ml-auto shrink-0 font-mono text-[0.7rem] text-muted-foreground/50">{{ formatDuration(o.seconds ?? 0) }}</span>
          </li>
        </ul>
        <p class="text-[0.72rem] text-muted-foreground/50">
          指纹：<span class="font-mono">{{ describeMatcher(c.q.matcher, readingLabels) }}</span>
        </p>
      </div>

      <!-- ===== Stage 1:自然语言回答 ===== -->
      <template v-if="c.stage === 'evidence' || c.stage === 'proposing'">
        <textarea
          v-model="c.answer"
          rows="2"
          :disabled="c.stage === 'proposing'"
          placeholder="用你自己的话说说这是什么——一次性的事、持续的脉络、属于哪个已有语境,或者还不确定,都可以直接写"
          class="w-full resize-y rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.9rem] outline-none focus:border-border disabled:opacity-50"
        ></textarea>

        <p v-if="c.error" class="text-[0.78rem] text-destructive">{{ c.error }}</p>

        <div v-if="c.muteConfirm" class="flex items-center justify-between gap-2 rounded-md border border-border/40 bg-background/50 px-3 py-2">
          <span class="text-[0.78rem] text-muted-foreground">确认后不再就这个{{ isRecurrence(c.q) ? '探针' : '指纹' }}发问；原始活动记录不受影响，仍会如实出现在回顾里。</span>
          <div class="flex shrink-0 gap-2">
            <button class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground hover:text-foreground" :disabled="c.busy" @click="c.muteConfirm = false">取消</button>
            <button class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-foreground disabled:opacity-50" :disabled="c.busy" @click="confirmMute(c)">确认静音</button>
          </div>
        </div>

        <div class="flex items-center justify-end gap-2">
          <button
            v-if="c.expired"
            class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-foreground"
            @click="load()"
          >刷新问题</button>
          <button
            class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
            :disabled="c.stage === 'proposing'"
            title="不写入任何内容,下次可能还会问"
            @click="remove(c)"
          >跳过</button>
          <button
            class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
            :disabled="c.stage === 'proposing' || c.muteConfirm"
            title="别再问这个"
            @click="c.muteConfirm = true"
          >别再问</button>
          <button
            class="glass-control cursor-pointer px-3 py-1 text-[0.75rem] text-foreground transition-colors disabled:opacity-50"
            :disabled="c.stage === 'proposing' || !c.answer.trim() || !c.q.windowKey || c.expired"
            @click="propose(c)"
          >{{ c.stage === 'proposing' ? '整理中…' : '整理成变更' }}</button>
        </div>
        <p v-if="c.stage === 'proposing'" class="text-[0.72rem] text-muted-foreground/60">
          正在把你的解释整理成结构化变更——这一步不会写入任何知识,整理好后由你逐项确认。
        </p>
      </template>

      <!-- ===== Stage 2:提案审阅(逐项编辑/取消,确认后才提交) ===== -->
      <template v-else-if="c.stage === 'review' || c.stage === 'committing'">
        <div class="flex flex-col gap-3 border-t border-border/40 pt-3">
          <p v-if="c.items.length === 0" class="text-[0.85rem] text-muted-foreground">
            这次没有需要保存的变更。可以直接关掉,或回去补充说明。
          </p>

          <ProposalReview
            :proposal="c.proposal"
            :items="c.items"
            :strands="strands"
            :reading-labels="readingLabels"
            :locked="c.stage === 'committing'"
            :failed-op-id="c.failedOpId"
          />

          <p v-if="c.error" class="text-[0.78rem] text-destructive">{{ c.error }}</p>

          <div class="flex items-center justify-end gap-2">
            <button
              v-if="c.items.length === 0"
              class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground"
              @click="remove(c)"
            >关掉</button>
            <button
              v-if="c.conflict"
              class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-foreground disabled:opacity-50"
              :disabled="c.stage === 'committing'"
              title="基于最新知识状态重新整理提案,再重新审阅"
              @click="reproposeAfterConflict(c)"
            >重新加载最新知识并重新审阅</button>
            <button
              class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
              :disabled="c.stage === 'committing'"
              title="回去修改回答(当前提案会丢弃)"
              @click="backToAnswer(c)"
            >返回修改回答</button>
            <button
              class="glass-control cursor-pointer px-3 py-1 text-[0.75rem] text-foreground transition-colors disabled:opacity-50"
              :disabled="c.stage === 'committing' || !canCommit(c.items)"
              @click="commit(c)"
            >{{ c.stage === 'committing' ? '提交中…' : `确认保存 ${selectedOps(c.items).length} 项` }}</button>
          </div>
        </div>
      </template>

      <!-- ===== 提交成功:真实 ID/path 回读 ===== -->
      <template v-else-if="c.stage === 'done'">
        <div class="flex flex-col gap-2 border-t border-border/40 pt-3">
          <p class="text-[0.85rem]">已保存 ✓</p>
          <ul class="flex flex-col gap-0.5">
            <li v-for="(line, i) in c.summary" :key="i" class="text-[0.78rem] text-muted-foreground">{{ line }}</li>
          </ul>
          <div class="flex justify-end">
            <button class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground hover:text-foreground" @click="remove(c)">收好了</button>
          </div>
        </div>
      </template>
    </div>
  </DashboardCard>
</template>
