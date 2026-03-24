type AgentShareRecord = {
  id: string
  token: string
  fileName: string
  filePath: string
  slug?: string | null
  publicBaseUrl: string
  fileSize: number
  createdAtUtc: string
  expiresAtUtc?: string | null
  maxUses?: number | null
  useCount: number
  state: string
  publishMode: string
  brokenReason?: string | null
  lastAccessedAtUtc?: string | null
}

type AgentRuntimeSnapshot = {
  shares: AgentShareRecord[]
  transfers: Array<Record<string, unknown>>
  settings: Record<string, unknown>
  cloudflared: Record<string, unknown>
}

type CloudflareDomainOption = {
  zoneId: string
  name: string
}

type CloudflareManagedStatus = {
  loggedIn: boolean
  message: string
  accountId?: string | null
  zoneId?: string | null
  zoneName?: string | null
  domains?: CloudflareDomainOption[] | null
  configuredHostname?: string | null
  configuredTunnelName?: string | null
}

type CloudflareManagedAvailability = {
  domain: string
  subdomain: string
  hostname: string
  tunnelName: string
  tunnelExists: boolean
  hostnameExists: boolean
  message: string
}

type AgentBridge = {
  getRuntime(): Promise<AgentRuntimeSnapshot>
  getShares(): Promise<AgentShareRecord[]>
  createShare(filePath: string, publishMode?: string): Promise<{ share: AgentShareRecord; url: string }>
  revokeShare(shareId: string): Promise<void>
  getSettings(): Promise<Record<string, unknown>>
  saveSettings(settings: Record<string, unknown>): Promise<void>
  getPublishProfiles(): Promise<Array<Record<string, unknown>>>
  savePublishProfile(mode: string, profile: Record<string, unknown>): Promise<void>
  detectCloudflared(): Promise<Record<string, unknown>>
  installCloudflared(): Promise<Record<string, unknown>>
  updateCloudflared(): Promise<Record<string, unknown>>
  startCloudflareLogin(): Promise<Record<string, unknown>>
  getManagedCloudflareStatus(): Promise<CloudflareManagedStatus>
  checkManagedTunnelAvailability(domain: string, subdomain: string): Promise<CloudflareManagedAvailability>
  createManagedTunnel(domain: string, subdomain: string): Promise<Record<string, unknown>>
  connectRuntime(onMessage: (event: Record<string, unknown>) => void): () => void
}

const agentBaseUrl = import.meta.env.VITE_AGENT_BASE_URL ?? 'http://127.0.0.1:46430'
const runtimeSocketUrl = `${agentBaseUrl.replace(/^http/, 'ws')}/ws/runtime`

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${agentBaseUrl}${path}`, {
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
    ...init,
  })

  if (!response.ok) {
    const message = await response.text()
    throw new Error(message || `Request failed with status ${response.status}`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

function createBrowserBridge(): AgentBridge {
  return {
    getRuntime: () => request<AgentRuntimeSnapshot>('/api/runtime'),
    getShares: () => request<AgentShareRecord[]>('/api/shares'),
    createShare: (filePath: string, publishMode?: string) =>
      request<{ share: AgentShareRecord; url: string }>('/api/shares', {
        method: 'POST',
        body: JSON.stringify({ filePath, publishMode }),
      }),
    revokeShare: (shareId: string) =>
      request<void>(`/api/shares/${shareId}`, { method: 'DELETE' }),
    getSettings: () => request<Record<string, unknown>>('/api/settings'),
    saveSettings: (settings: Record<string, unknown>) =>
      request<void>('/api/settings', {
        method: 'PUT',
        body: JSON.stringify({ settings }),
      }),
    getPublishProfiles: () => request<Array<Record<string, unknown>>>('/api/publish-profiles'),
    savePublishProfile: (mode: string, profile: Record<string, unknown>) =>
      request<void>(`/api/publish-profiles/${mode}`, {
        method: 'PUT',
        body: JSON.stringify(profile),
      }),
    detectCloudflared: () => request<Record<string, unknown>>('/api/cloudflared/detect', { method: 'POST' }),
    installCloudflared: () => request<Record<string, unknown>>('/api/cloudflared/install', { method: 'POST' }),
    updateCloudflared: () => request<Record<string, unknown>>('/api/cloudflared/update', { method: 'POST' }),
    startCloudflareLogin: () => request<Record<string, unknown>>('/api/cloudflared/login', { method: 'POST' }),
    getManagedCloudflareStatus: () => request<CloudflareManagedStatus>('/api/cloudflared/managed-status'),
    checkManagedTunnelAvailability: (domain: string, subdomain: string) =>
      request<CloudflareManagedAvailability>('/api/cloudflared/managed-check', {
        method: 'POST',
        body: JSON.stringify({ domain, subdomain }),
      }),
    createManagedTunnel: (domain: string, subdomain: string) =>
      request<Record<string, unknown>>('/api/cloudflared/managed-tunnel', {
        method: 'POST',
        body: JSON.stringify({ domain, subdomain }),
      }),
    connectRuntime: (onMessage: (event: Record<string, unknown>) => void) => {
      const socket = new WebSocket(runtimeSocketUrl)
      socket.addEventListener('message', (event) => {
        onMessage(JSON.parse(event.data as string) as Record<string, unknown>)
      })
      return () => socket.close()
    },
  }
}

export const agentBridge: AgentBridge =
  typeof window !== 'undefined' && typeof window.instantFileShare !== 'undefined'
    ? window.instantFileShare
    : createBrowserBridge()

export type {
  AgentBridge,
  AgentRuntimeSnapshot,
  AgentShareRecord,
  CloudflareManagedAvailability,
  CloudflareManagedStatus,
}
