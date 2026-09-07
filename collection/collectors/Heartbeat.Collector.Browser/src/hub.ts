// Browser ExternalHost binding 的 loopback 发现与 Collector Protocol adapter。

import type { BrowserHubAdapter, BrowserProtocolDeliveryRequest } from './delivery'
import { uploadWithBrowserProtocol } from './protocol'

/** 与 Hub 侧 ExternalHostProtocolWorker.PortRange 一致：基准端口起向上探测的端口数。 */
export const PORT_RANGE = 10

/** 单端口探测超时：loopback 应答在毫秒级，超时即视为无人/非 hub。 */
const PROBE_TIMEOUT_MS = 1500
/** 只认通用 ExternalHost 发现端点，避免把别的 loopback 服务当成 Hub。 */
export async function probeHub(port: number): Promise<boolean> {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/v1/collector-protocol/external-host`, {
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
    })
    if (!res.ok) return false
    const body = (await res.json()) as { binding?: unknown; protocolMajors?: unknown }
    return body.binding === 'external-host' &&
      Array.isArray(body.protocolMajors) && body.protocolMajors.includes(1)
  } catch {
    return false
  }
}

/**
 * 在 [basePort, basePort + PORT_RANGE) 内并发探测，返回首个（编号最小的）hub 端口；无则 null。
 * hub 端口被占时顺延到下一个，所以低编号优先即"hub 实际所在"。
 */
export async function discoverHub(basePort: number): Promise<number | null> {
  const ports = Array.from({ length: PORT_RANGE }, (_, i) => basePort + i).filter(
    (p) => p <= 65535,
  )
  const results = await Promise.all(ports.map(probeHub))
  const index = results.findIndex(Boolean)
  return index >= 0 ? ports[index] : null
}

/** 优先复用缓存端口；不兼容时在约定范围内寻找协议匹配的 hub。 */
export async function findCompatibleHub(
  basePort: number,
  targetPort: number,
): Promise<number | null> {
  if (await probeHub(targetPort)) return targetPort
  return discoverHub(basePort)
}

/** Production loopback HTTP adapter；wire 路由与响应 shape 不进入 BrowserDelivery interface。 */
export class LoopbackBrowserHubAdapter implements BrowserHubAdapter {
  findCompatibleHub(basePort: number, targetPort: number): Promise<number | null> {
    return findCompatibleHub(basePort, targetPort)
  }

  deliverProtocol(request: BrowserProtocolDeliveryRequest) {
    return uploadWithBrowserProtocol(
      request.port,
      request.appIdentityKey,
      request.externalHostIdentity,
      request.snapshots,
      request.previousSession,
      request.previousActivationAttempt,
      request.previousPublishAttempt,
      request.persistActivationAttempt,
      request.persistPublishAttempt,
      request.applySpec,
      request.pendingGap,
      request.persistGapAttempt,
    )
  }
}
