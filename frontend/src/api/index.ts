import { Client, ApiException, CalendarWindowError, DailyRecapResponse, DailyReportResponse, WeeklyReportResponse, AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse, SegmentResponse, UpdateMySettingsRequest, AskingQuestionsResponse, KnowledgeProposalResponse, ProposeCorrectionRequest, ProposeFromQuestionRequest, CommitChangeSetRequest, CommitChangeSetResponse, ChangeSetErrorResponse, KnowledgeErrorResponse, CreateStrandRequest, UpdateStrandRequest, MoveStrandRequest, EndStrandRequest, MuteMatcherRequest, StrandResponse, EpisodeResponse, ProbeResponse, PromoteEpisodeResponse, CreateEpisodeRequest, UpdateEpisodeRequest, RelateEpisodeRequest, CreateProbeRequest, ResolveProbeRequest, PromoteEpisodeRequest, type ICreateStrandRequest, type IUpdateStrandRequest, type IMoveStrandRequest, type IEndStrandRequest, type IMatcherDto, type IKnowledgeOperationDto, type IChangeSetErrorResponse, type IKnowledgeErrorResponse, type ICreateEpisodeRequest, type IUpdateEpisodeRequest, type IRelateEpisodeRequest, type ICreateProbeRequest, type IResolveProbeRequest, type IPromoteEpisodeRequest } from './client'
import {
  AppCatalogAdminErrorResponse,
  AppCatalogExportRequest,
  AppCatalogOverrideSetRequest,
  type AppCatalogAdminAuditResponse,
  type AppCatalogAdminInventoryResponse,
  type AppCatalogExportResponse,
  type AppCatalogReconciliationResponse,
} from './client'
import { authStore } from '../stores/auth'
import { createSseFrameParser, type SseFrame } from './sse'
import type { CalendarWindowEnvelope } from '../calendar/localCalendarWindow'

// ===== Error model =====
// 取数失败的归一形态。让取数策略层能区分"出错"(network/http/parse)与"没数据"(空数组)。
export type ApiError =
  | { kind: 'network' }               // fetch 抛 TypeError:断网 / DNS / CORS
  | { kind: 'http'; status: number }  // 4xx/5xx:NSwag ApiException.status
  | { kind: 'parse' }                 // 响应体不是预期结构
  | { kind: 'calendar'; code: string; message: string }

const CALENDAR_WINDOW_ERROR_CODES = new Set([
  'unsupported_calendar_window',
  'invalid_local_date',
  'unsupported_timezone',
  'nonexistent_civil_date',
  'calendar_rules_mismatch',
])

/** 把 NSwag ApiException、原生 fetch TypeError、以及其它意外统一成 ApiError。 */
export function toApiError(e: unknown): ApiError {
  if (e instanceof CalendarWindowError) {
    return { kind: 'calendar', code: e.code, message: e.message }
  }
  if (e instanceof KnowledgeErrorResponse
    && typeof e.code === 'string'
    && CALENDAR_WINDOW_ERROR_CODES.has(e.code)
    && typeof e.message === 'string') {
    return { kind: 'calendar', code: e.code, message: e.message }
  }
  if (ApiException.isApiException(e)) {
    if (e.result instanceof CalendarWindowError) {
      return { kind: 'calendar', code: e.result.code, message: e.result.message }
    }
    const calendarError = parseCalendarWindowError(e.response)
    if (calendarError) return calendarError
    return { kind: 'http', status: e.status }
  }
  if (e instanceof TypeError) return { kind: 'network' }
  return { kind: 'parse' }
}

/**
 * 手写 transport 没有 NSwag 的 DTO 反序列化层，但 calendar error 仍是同一个稳定 JSON contract。
 * 只在 code/message 完整时接受，避免把任意 400 body 误报成日历规则错误。
 */
function parseCalendarWindowError(response: string | undefined): ApiError | null {
  if (!response) return null
  try {
    const value = JSON.parse(response) as { code?: unknown; message?: unknown } | null
    return value && typeof value.code === 'string' && CALENDAR_WINDOW_ERROR_CODES.has(value.code)
      && typeof value.message === 'string'
      ? { kind: 'calendar', code: value.code, message: value.message }
      : null
  } catch {
    return null
  }
}

// ===== Base URL =====
const BASE_URL = ''
const API_BASE = '/api/v1'

// ===== Auth-aware fetch wrapper =====
const authHttp = {
  async fetch(url: RequestInfo, init?: RequestInit): Promise<Response> {
    const token = authStore.token.value
    if (token) {
      const headers = new Headers(init?.headers)
      headers.set('Authorization', `Bearer ${token}`)
      init = { ...init, headers }
    }

    let response = await fetch(url, init)

    if (response.status === 401) {
      const refreshed = await authStore.tryRefresh()
      if (refreshed) {
        const headers = new Headers(init?.headers)
        headers.set('Authorization', `Bearer ${authStore.token.value}`)
        response = await fetch(url, { ...init, headers })
      } else {
        authStore.clearAuth()
      }
    }

    return response
  },
}

export interface CollectorAuthorizationField {
  name: string
  label: string
  isSecret: boolean
  inputMode?: 'none' | 'text' | 'decimal' | 'numeric' | 'tel' | 'search' | 'email' | 'url' | null
}

export interface CollectorAuthorizationChallenge {
  interactionId: string
  kind: 'Credentials' | 'VerificationCode' | 'Notice'
  title: string
  message?: string | null
  fields: CollectorAuthorizationField[]
}

export interface ManagedCollectorStatus {
  packageId: string
  displayName: string
  summary: string
  latestVersion?: string | null
  isInstalled: boolean
  installedVersion?: string | null
  collectorInstanceId?: string | null
  phase: string
  statusDetail?: string | null
  authorization?: CollectorAuthorizationChallenge | null
  currentActivity?: {
    title?: string | null
    identityKey?: string | null
    startTime: string
    endTime: string
    attributes?: unknown
  } | null
}

export interface HostManagementOperation {
  operationId: string
  packageId: string
  kind: 'Install' | 'Retry' | 'Uninstall' | 'SubmitAuthorization'
  phase: 'Pending' | 'Running' | 'Committing' | 'Succeeded' | 'Cancelled' | 'Failed'
  isTerminal: boolean
  failure?: string | null
}

export async function fetchManagedOperations(signal?: AbortSignal): Promise<HostManagementOperation[]> {
  const response = await authHttp.fetch('/hub/api/v1/operations', { signal })
  if (!response.ok) throw new ApiException('Hub operation query failed.', response.status, await response.text(), {}, null)
  return await response.json() as HostManagementOperation[]
}

export async function cancelManagedOperation(operationId: string): Promise<string> {
  const response = await authHttp.fetch(`/hub/api/v1/operations/${encodeURIComponent(operationId)}/cancellation`, { method: 'POST' })
  if (!response.ok && response.status !== 409) throw new ApiException('Hub operation cancellation failed.', response.status, await response.text(), {}, null)
  return await response.json() as string
}

export async function fetchManagedCollectors(signal?: AbortSignal): Promise<ManagedCollectorStatus[]> {
  const response = await authHttp.fetch('/hub/api/v1/collectors', { signal })
  if (!response.ok) throw new ApiException('Hub Collector catalog request failed.', response.status, await response.text(), {}, null)
  return await response.json() as ManagedCollectorStatus[]
}

export async function installManagedCollector(packageId: string): Promise<HostManagementOperation> {
  const response = await authHttp.fetch(
    `/hub/api/v1/collectors/${encodeURIComponent(packageId)}/installation`,
    { method: 'POST' },
  )
  if (!response.ok) throw new ApiException('Hub Collector installation failed.', response.status, await response.text(), {}, null)
  return await response.json() as HostManagementOperation
}

export async function uninstallManagedCollector(packageId: string): Promise<HostManagementOperation> {
  const response = await authHttp.fetch(
    `/hub/api/v1/collectors/${encodeURIComponent(packageId)}/installation`,
    { method: 'DELETE' },
  )
  if (!response.ok) throw new ApiException('Hub Collector uninstall failed.', response.status, await response.text(), {}, null)
  return await response.json() as HostManagementOperation
}

export async function retryManagedCollector(packageId: string): Promise<HostManagementOperation> {
  const response = await authHttp.fetch(
    `/hub/api/v1/collectors/${encodeURIComponent(packageId)}/activation`,
    { method: 'POST' },
  )
  if (!response.ok) throw new ApiException('Hub Collector activation retry failed.', response.status, await response.text(), {}, null)
  return await response.json() as HostManagementOperation
}

export async function submitCollectorAuthorization(
  collectorInstanceId: string,
  interactionId: string,
  values: Record<string, string>,
): Promise<HostManagementOperation> {
  const response = await authHttp.fetch(
    `/hub/api/v1/collector-instances/${encodeURIComponent(collectorInstanceId)}/authorization/${encodeURIComponent(interactionId)}`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ values }),
    },
  )
  if (!response.ok) throw new ApiException('Hub authorization response failed.', response.status, await response.text(), {}, null)
  return await response.json() as HostManagementOperation
}

const client = new Client(BASE_URL, authHttp)

// Re-export generated types
export type { AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse, DailyRecapResponse, DailyReportResponse, WeeklyReportResponse, SegmentResponse }
export type { AppDurationItem } from './client'

export interface AppSummary {
  appId: number
  appName: string
  totalSeconds: number
}

/**
 * deviceId 归一：0 = "全部设备"（看板默认视图），对服务端即"不传 deviceId"→ 跨设备聚合。
 * 收口在 API 边界,composable 与组件层可以一路传 0 不做特判。
 */
function deviceScope(deviceId?: number): number | undefined {
  return deviceId ? deviceId : undefined
}

/** 键频项的 null 归一:生成类型里 keys / code / count 都可空,收敛成密实数组。 */
export interface KeyFrequencyItem {
  code: number
  count: number
}
function normalizeKeyFrequency(res: { keys?: { code?: number; count?: number }[] }): KeyFrequencyItem[] {
  return (res.keys ?? []).map(k => ({ code: k.code ?? 0, count: k.count ?? 0 }))
}

// ===== API Functions (authenticated, own data) =====

export async function fetchDevices(): Promise<DeviceInfoResponse[]> {
  return client.getDevices()
}

export async function fetchApps(): Promise<AppInfoResponse[]> {
  return client.getApps()
}

export async function fetchDeviceStatus(deviceId: number): Promise<DeviceStatusResponse> {
  return client.getDevice(deviceId)
}

export async function fetchUsage(params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<AppUsageResponse[]> {
  return client.getUsage(
    deviceScope(params.deviceId),
    params.start ? new Date(params.start) : undefined,
    params.end ? new Date(params.end) : undefined,
  )
}

export async function fetchDailyReport(params: {
  deviceId?: number
  window: CalendarWindowEnvelope<'day'>
}): Promise<DailyReportResponse> {
  const window = params.window
  return client.getDailyReport(
    deviceScope(params.deviceId),
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
  )
}

export async function fetchWeeklyReport(params: {
  deviceId?: number
  window: CalendarWindowEnvelope<'week'>
}): Promise<WeeklyReportResponse> {
  const window = params.window
  return client.getWeeklyReport(
    deviceScope(params.deviceId),
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
  )
}

function getIconUrl(username: string, appId: number): string {
  return `${API_BASE}/users/${encodeURIComponent(username)}/apps/${appId}/icon`
}

/**
 * 图标也服从 Dashboard Visibility。不能把 URL 直接交给 <img>：private owner 的
 * Bearer 只由 authHttp 注入，浏览器的图片子资源请求不会自动携带它。
 */
export async function fetchAppIcon(username: string, appId: number): Promise<Blob | null> {
  const res = await authHttp.fetch(getIconUrl(username, appId))
  if (res.status === 404) return null
  if (!res.ok) throw new ApiException('Icon request failed.', res.status, await res.text(), {}, null)
  return res.blob()
}

// ===== Recap（ADR-023，读写按动词拆分随 ADR-042，窗口身份随 ADR-044）=====

function calendarWindowSearchParams(window: CalendarWindowEnvelope<'day'>): URLSearchParams {
  return new URLSearchParams({
    version: String(window.version),
    kind: window.kind,
    localDate: window.localDate,
    timeZone: window.timeZone,
    start: window.start,
    endExclusive: window.endExclusive,
  })
}

/**
 * 读取这一天的 Recap。纯读——服务端在这条路径上零 LLM、零写库（ADR-042 §2），`force` 已取消，
 * 生成只由 streamDailyRecapGeneration 触发。三态由字段组合表达：
 * `isEmpty` → 空日；`!isEmpty && narrative == null` → 有数据但从未生成；否则 → 有叙事。
 */
export async function fetchDailyRecap(params: {
  window: CalendarWindowEnvelope<'day'>
}): Promise<DailyRecapResponse> {
  const window = params.window
  return client.getDailyRecap(
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
  )
}

/** 公开 Recap 只读取 owner 已生成的缓存，匿名访问永不触发 LLM 生成。 */
export async function fetchPublicDailyRecap(username: string, params: {
  window: CalendarWindowEnvelope<'day'>
}): Promise<DailyRecapResponse> {
  const window = params.window
  return client.getUserDailyRecap(
    username,
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
  )
}

// ===== Recap 流式生成（ADR-042 §4）=====
// POST /recaps/daily/generate 不在 OpenAPI 里（NSwag 无法为流生成有意义的签名），契约靠
// docs/api.md 和这份手写 wrapper 维持。用 fetch + ReadableStream 而非 EventSource：后者只能
// GET 且带不了 Authorization 头，而认证是 Bearer。

/** 并发撞锁（HTTP 409）的兜底文案；服务端也会给一句可读原因，优先用服务端那句。 */
export const RECAP_ALREADY_GENERATING_MESSAGE = '这一天正在生成中'

/** 生成失败但服务端没给可读原因时的兜底文案。 */
export const RECAP_GENERATION_FAILED_MESSAGE = '生成失败，请稍后重试'

export interface RecapStreamHandlers {
  /**
   * 推理增量（ADR-042 §9）：思考模式的模型会先吐 `reasoning_content`——实测 8.5KB 的 digest
   * 前 175 秒只有推理、第一个正文 token 才在 +175.5s 出现。这个事件存在是为了让那段沉默
   * 有东西可显示；它与 delta 一样是增量，且首个 delta 之后不会再来。
   */
  onThinking?: (text: string) => void
  /** 增量文本：原样追加，段落由上层对累积文本重算（不做打字机动画）。 */
  onDelta?: (text: string) => void
  /** 生成完成：与 GET 同一个 DTO 形状，上层只需一份渲染逻辑。 */
  onDone?: (recap: DailyRecapResponse) => void
  /** 生成域失败：响应头一发出 502 就不再可能，原因只能走流内事件（ADR-042 §4）。 */
  onError?: (message: string) => void
}

/**
 * 消费一次 Recap 流式生成。
 *
 * 失败分工：鉴权/参数/并发这类 4xx 抛 `ApiException`（上层用 `status === 409` 识别撞锁），
 * 生成域的失败走 `onError`，`abort` 静默返回——切日期与卸载是意图，不是错误。
 * 认证复用 `authHttp`：401 的刷新重试只可能发生在读流之前（那时响应体还没被消费），
 * **流一旦开始就不重试**——重放一次生成等于再烧一次 token。
 * 心跳（`event: ping`）与未知事件类型一律吞掉。
 */
export async function streamDailyRecapGeneration(
  params: { window: CalendarWindowEnvelope<'day'>; signal?: AbortSignal },
  handlers: RecapStreamHandlers,
): Promise<void> {
  const searchParams = calendarWindowSearchParams(params.window)

  try {
    const res = await authHttp.fetch(`${API_BASE}/recaps/daily/generate?${searchParams}`, {
      method: 'POST',
      headers: { Accept: 'text/event-stream' },
      signal: params.signal,
    })
    if (!res.ok) throw new ApiException('Recap generation failed.', res.status, await res.text(), {}, null)
    if (!res.body) throw new ApiException('Recap generation returned no stream.', res.status, '', {}, null)

    const reader = res.body.getReader()
    const parser = createSseFrameParser()
    for (;;) {
      const { done, value } = await reader.read()
      if (done) {
        dispatchRecapFrames(parser.flush(), handlers)
        return
      }
      if (value) dispatchRecapFrames(parser.push(value), handlers)
    }
  } catch (e) {
    if (isAbortError(e, params.signal)) return
    throw e
  }
}

/** 帧 → 四类回调。ping/未知事件类型吞掉，坏帧跳过（一帧解不出来不该杀掉整条流）。 */
function dispatchRecapFrames(frames: SseFrame[], handlers: RecapStreamHandlers): void {
  for (const frame of frames) {
    // ping 是心跳：.NET 的 SseItem 输出不了 SSE 注释行，所以它占了一个事件类型（ADR-042 §4）
    if (frame.event === 'ping') continue
    const payload = tryParseJsonObject(frame.data)
    if (!payload) continue
    if (frame.event === 'thinking') {
      const text = typeof payload.thinking === 'string' ? payload.thinking : ''
      if (text) handlers.onThinking?.(text)
    } else if (frame.event === 'delta') {
      const text = typeof payload.delta === 'string' ? payload.delta : ''
      if (text) handlers.onDelta?.(text)
    } else if (frame.event === 'done') {
      if (payload.recap) handlers.onDone?.(DailyRecapResponse.fromJS(payload.recap))
    } else if (frame.event === 'error') {
      const message = typeof payload.message === 'string' && payload.message ? payload.message : RECAP_GENERATION_FAILED_MESSAGE
      handlers.onError?.(message)
    }
  }
}

function tryParseJsonObject(text: string): Record<string, unknown> | null {
  if (!text) return null
  try {
    const parsed = JSON.parse(text)
    return parsed && typeof parsed === 'object' ? parsed as Record<string, unknown> : null
  } catch {
    return null
  }
}

/** abort 在不同实现里形态不一（DOMException / 自造 Error），signal 自己的状态最可靠。 */
function isAbortError(e: unknown, signal?: AbortSignal): boolean {
  if (signal?.aborted) return true
  return (e as { name?: string } | null)?.name === 'AbortError'
}

/**
 * 流式生成的 HTTP/网络层失败 → 可读文案。生成域的失败不经过这里（走流内 error 事件），
 * 这里只翻译"流还没开始就没了"的那几种：409 是并发撞锁，其余按状态码兜底。
 */
export function recapGenerationErrorMessage(e: unknown): string {
  const err = toApiError(e)
  if (err.kind === 'calendar') return err.message
  if (err.kind === 'network') return '网络连接失败，请检查网络后重试'
  if (err.kind === 'http' && err.status === 409) return readableErrorBody(e) ?? RECAP_ALREADY_GENERATING_MESSAGE
  if (err.kind === 'http') return readableErrorBody(e) ?? `服务器返回错误（${err.status}），请稍后重试`
  return RECAP_GENERATION_FAILED_MESSAGE
}

/** 取服务端给的可读 body。`TypedResults.Conflict(string)` 输出的是带引号的 JSON 字符串。 */
function readableErrorBody(e: unknown): string | null {
  if (!ApiException.isApiException(e)) return null
  const text = (e.response ?? '').trim()
  if (!text || text.startsWith('<')) return null // HTML 错误页不是给人看的文案
  try {
    const parsed = JSON.parse(text)
    return typeof parsed === 'string' && parsed ? parsed : null
  } catch {
    return text
  }
}

// ===== Strand 知识层（ADR-028/029/031）=====
// owner-only：确认写知识 + 发问/整理烧 LLM token，无 public 版。
// questions/propose 都携带同一个完整 day envelope；问题响应的 WindowKey 是提交凭据，
// 页面刷新 correlation identity 不参与 transport 或持久化身份。
// 已有 Strand 一律按 UUIDv7 定位（ADR-031）——按名收敛的旧 bindStrand 已退役。

export type { IMatcherDto, IMatcherStepDto, IStrandResponse, ICreateStrandRequest, IUpdateStrandRequest, IMoveStrandRequest, IEndStrandRequest, IKnowledgeErrorResponse } from './client'
export type { IAskingQuestionResponse, IEvidenceObservationDto, IKnowledgeProposalResponse, IKnowledgeOperationDto, IOperationResultResponse, ICommitChangeSetResponse, IChangeSetErrorResponse, IStrandRefDto, IEpisodeRefDto } from './client'
export type { IEpisodeResponse, IProbeResponse, IPromoteEpisodeResponse, ICreateEpisodeRequest, IUpdateEpisodeRequest, IRelateEpisodeRequest, ICreateProbeRequest, IResolveProbeRequest, IPromoteEpisodeRequest } from './client'
export { StrandRefDto, KnowledgeOperationDto } from './client'

/** 当日证据卡问题（ADR-031 §6 两阶段第一步）：真实活动簇的时段与跨 Source 观察。 */
export async function fetchDailyQuestions(params: {
  window: CalendarWindowEnvelope<'day'>
}): Promise<AskingQuestionsResponse> {
  const window = params.window
  return client.getDailyQuestions(
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
  )
}

/**
 * 两阶段第二步：把用户对证据卡的自然语言回答交给服务端整理成可编辑提案。零写入。
 * WindowKey 须来自问题响应，window 须是提交时的当前 Calendar Context；服务端严格比较两者，
 * 防止日期或 timezone 已变化后把旧证据解释成新窗口。失败时生成客户端会直接抛出 typed
 * KnowledgeErrorResponse；兼容层也保留对旧式 ApiException.result 的读取。
 */
export async function proposeFromQuestion(questionId: string, params: {
  window: CalendarWindowEnvelope<'day'>
  windowKey: string
  answer: string
}): Promise<KnowledgeProposalResponse> {
  const window = params.window
  return client.proposeFromQuestion(
    questionId,
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
    ProposeFromQuestionRequest.fromJS({ windowKey: params.windowKey, answer: params.answer }),
  )
}

/**
 * Recap 纠正入口（ADR-031 §6）：把用户对某日回顾的自然语言纠正交给服务端整理成
 * 可编辑提案。零写入——证据上下文由服务端按同一个已验证 day window 锁定，不提交散文 patch。
 */
export async function proposeCorrection(params: {
  window: CalendarWindowEnvelope<'day'>
  correction: string
}): Promise<KnowledgeProposalResponse> {
  const window = params.window
  return client.proposeCorrection(
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
    ProposeCorrectionRequest.fromJS({ correction: params.correction }),
  )
}

/**
 * 共享事务提交端（ADR-031 §6）：用户最终确认后提交选中的操作，全部成功才写入。
 * body 经生成类 toJSON 序列化——DateOnly 字段（startedOn/endedOn/localDate）必须输出
 * "yyyy-MM-dd"（本地日期分量），裸 JSON.stringify 会把 Date 变成 UTC datetime 被服务端拒收。
 * 失败响应是 ChangeSetErrorResponse（failedOpId 定位具体操作），塞进 ApiException.result。
 */
export async function commitChangeSet(operations: IKnowledgeOperationDto[]): Promise<CommitChangeSetResponse> {
  const body = CommitChangeSetRequest.fromJS({ operations })
  const res = await authHttp.fetch(`${API_BASE}/knowledge/changesets`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!res.ok) {
    const text = await res.text()
    throw new ApiException('Commit request failed.', res.status, text, {}, tryParseError(text, ChangeSetErrorResponse.fromJS))
  }
  return CommitChangeSetResponse.fromJS(await res.json())
}

function tryParseError<T>(text: string, fromJS: (data: unknown) => T): T | null {
  try {
    return fromJS(JSON.parse(text))
  } catch {
    return null
  }
}

/** 从抛出的错误里取 changesets 的结构化错误体（failedOpId + code）；非该形状返回 null。 */
export function changeSetErrorOf(e: unknown): IChangeSetErrorResponse | null {
  if (ApiException.isApiException(e) && e.result instanceof ChangeSetErrorResponse) return e.result
  return null
}

/** 从 generated client 直接抛出或手写 wrapper 包装的错误里取 KnowledgeErrorResponse。 */
export function knowledgeErrorOf(e: unknown): IKnowledgeErrorResponse | null {
  if (e instanceof KnowledgeErrorResponse) return e
  if (ApiException.isApiException(e) && e.result instanceof KnowledgeErrorResponse) return e.result
  return null
}

/** 整树读取：全部节点（含已结束时期）带 parent ID 与根到自身 path。 */
export async function fetchStrands(): Promise<StrandResponse[]> {
  return client.getStrands()
}

export async function createStrand(req: ICreateStrandRequest): Promise<StrandResponse> {
  return client.createStrand(CreateStrandRequest.fromJS(req))
}

export async function updateStrand(id: string, req: IUpdateStrandRequest): Promise<StrandResponse> {
  return client.updateStrand(id, UpdateStrandRequest.fromJS(req))
}

export async function moveStrand(id: string, req: IMoveStrandRequest): Promise<StrandResponse> {
  return client.moveStrand(id, MoveStrandRequest.fromJS(req))
}

export async function endStrand(id: string, req: IEndStrandRequest): Promise<StrandResponse> {
  return client.endStrand(id, EndStrandRequest.fromJS(req))
}

/** Mute 一个 Matcher（负向裁决）：别再就它发问。 */
export async function muteMatcher(matcher: IMatcherDto): Promise<void> {
  return client.muteMatcher(MuteMatcherRequest.fromJS({ matcher }))
}

// ===== Episode / Probe（ADR-031 §4/§5）=====

export type { EpisodeResponse, ProbeResponse, PromoteEpisodeResponse } from './client'

export async function fetchEpisodes(params: { date?: string; strandId?: string }): Promise<EpisodeResponse[]> {
  return client.getEpisodes(
    params.date ? new Date(params.date) : undefined,
    params.strandId,
  )
}

export async function createEpisode(req: ICreateEpisodeRequest): Promise<EpisodeResponse> {
  return client.createEpisode(CreateEpisodeRequest.fromJS(req))
}

export async function updateEpisode(id: string, req: IUpdateEpisodeRequest): Promise<EpisodeResponse> {
  return client.updateEpisode(id, UpdateEpisodeRequest.fromJS(req))
}

export async function relateEpisode(id: string, req: IRelateEpisodeRequest): Promise<EpisodeResponse> {
  return client.relateEpisode(id, RelateEpisodeRequest.fromJS(req))
}

export async function deleteEpisode(id: string, expectedVersion: number): Promise<void> {
  return client.deleteEpisode(id, expectedVersion)
}

export async function createProbe(episodeId: string, req: ICreateProbeRequest): Promise<ProbeResponse> {
  return client.createProbe(episodeId, CreateProbeRequest.fromJS(req))
}

export async function resolveProbe(id: string, req: IResolveProbeRequest): Promise<ProbeResponse> {
  return client.resolveProbe(id, ResolveProbeRequest.fromJS(req))
}

export async function promoteEpisode(id: string, req: IPromoteEpisodeRequest): Promise<PromoteEpisodeResponse> {
  return client.promoteEpisode(id, PromoteEpisodeRequest.fromJS(req))
}

// ===== Me（本人视角,ADR-025）=====
// GET /me 是懒建供给的触发点:登录后必须调一次,否则 User 行不存在,
// 本人的 /:username 看板会 404(可见性门查不到用户)。

export interface MeSettings {
  username: string
  isPublic: boolean
  isAdmin: boolean
}

export async function fetchMe(): Promise<MeSettings> {
  const res = await client.getMe()
  return {
    username: res.username ?? '',
    isPublic: res.isPublic ?? false,
    isAdmin: res.isAdmin ?? false,
  }
}

export async function updateMySettings(isPublic: boolean): Promise<MeSettings> {
  const res = await client.updateMySettings(UpdateMySettingsRequest.fromJS({ isPublic }))
  return {
    username: res.username ?? '',
    isPublic: res.isPublic ?? false,
    isAdmin: res.isAdmin ?? false,
  }
}

// ===== Deployment administration: App Catalog =====

export type {
  AppCatalogAdminAuditResponse,
  AppCatalogAdminInventoryResponse,
  AppCatalogExportResponse,
  AppCatalogReconciliationResponse,
}

export async function fetchAdminAppCatalog(): Promise<AppCatalogAdminInventoryResponse> {
  return client.getAdminAppCatalog()
}

export async function fetchAdminAppCatalogAudit(limit = 50): Promise<AppCatalogAdminAuditResponse[]> {
  return (await client.getAdminAppCatalogAudit(limit)).entries ?? []
}

export async function previewAdminAppCatalogOverride(
  identityKey: string,
  targetAppKey: string,
  newAppDisplayName?: string,
): Promise<AppCatalogReconciliationResponse> {
  return client.previewAdminAppCatalogOverride(
    identityKey,
    AppCatalogOverrideSetRequest.fromJS({ targetAppKey, newAppDisplayName }),
  )
}

export async function setAdminAppCatalogOverride(
  identityKey: string,
  targetAppKey: string,
  newAppDisplayName?: string,
): Promise<AppCatalogReconciliationResponse> {
  return client.setAdminAppCatalogOverride(
    identityKey,
    AppCatalogOverrideSetRequest.fromJS({ targetAppKey, newAppDisplayName }),
  )
}

export async function previewDeleteAdminAppCatalogOverride(
  identityKey: string,
): Promise<AppCatalogReconciliationResponse> {
  return client.previewDeleteAdminAppCatalogOverride(identityKey)
}

export async function deleteAdminAppCatalogOverride(
  identityKey: string,
): Promise<AppCatalogReconciliationResponse> {
  return client.deleteAdminAppCatalogOverride(identityKey)
}

export async function exportAdminAppCatalogCandidate(
  selectedIdentityKeys: string[],
): Promise<AppCatalogExportResponse> {
  return client.exportAdminAppCatalogCandidate(
    AppCatalogExportRequest.fromJS({ selectedIdentityKeys }),
  )
}

export function appCatalogAdminErrorOf(error: unknown): AppCatalogAdminErrorResponse | null {
  return ApiException.isApiException(error) && error.result instanceof AppCatalogAdminErrorResponse
    ? error.result
    : null
}

// ===== Public API Functions (no auth required, by username) =====
// 统一走 NSwag 生成的 client 方法(响应类型由 OpenAPI schema 保证);
// Daily / Weekly Report 的 typed wrapper 都委托 generated client。

export async function fetchPublicDevices(username: string): Promise<DeviceInfoResponse[]> {
  return client.getUserDevices(username)
}

export async function fetchPublicApps(username: string): Promise<AppInfoResponse[]> {
  return client.getUserApps(username)
}

export async function fetchPublicDailyReport(username: string, params: {
  deviceId?: number
  window: CalendarWindowEnvelope<'day'>
}): Promise<DailyReportResponse> {
  const window = params.window
  return client.getUserDailyReport(
    username,
    deviceScope(params.deviceId),
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
  )
}

export async function fetchPublicWeeklyReport(username: string, params: {
  deviceId?: number
  window: CalendarWindowEnvelope<'week'>
}): Promise<WeeklyReportResponse> {
  const window = params.window
  return client.getUserWeeklyReport(
    username,
    deviceScope(params.deviceId),
    window.version,
    window.kind,
    window.localDate,
    window.timeZone,
    new Date(window.start),
    new Date(window.endExclusive),
  )
}

export async function fetchPublicDeviceStatus(username: string, deviceId: number): Promise<DeviceStatusResponse> {
  return client.getUserDeviceStatus(username, deviceId)
}

export async function fetchPublicUsage(username: string, params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<AppUsageResponse[]> {
  return client.getUserUsage(
    username,
    deviceScope(params.deviceId),
    params.start ? new Date(params.start) : undefined,
    params.end ? new Date(params.end) : undefined,
  )
}

export async function fetchPublicSegments(username: string, params: {
  deviceId?: number
  source?: string
  appId?: number
  start?: string
  end?: string
}): Promise<SegmentResponse[]> {
  return client.getUserSegments(
    username,
    deviceScope(params.deviceId),
    params.source,
    params.appId,
    params.start ? new Date(params.start) : undefined,
    params.end ? new Date(params.end) : undefined,
  )
}

export async function fetchPublicKeyFrequency(username: string, params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<KeyFrequencyItem[]> {
  const res = await client.getUserKeyFrequency(
    username,
    deviceScope(params.deviceId),
    params.start ? new Date(params.start) : undefined,
    params.end ? new Date(params.end) : undefined,
  )
  return normalizeKeyFrequency(res)
}
