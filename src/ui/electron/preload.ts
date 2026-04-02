import { contextBridge, ipcRenderer } from 'electron'

type PublishMode = 'QuickTunnel' | 'ManagedCloudflare' | 'Manual'
type ShareKind = 'File' | 'Folder'
type FolderShareEntryPoint = 'Browse' | 'Zip'

type ShareRecord = {
  id: string
  token: string
  fileName: string
  filePath: string
  slug?: string | null
  publicBaseUrl: string
  fileSize: number
  shareKind: ShareKind
  canBrowseFolderContents: boolean
  canDownloadFolderAsZip: boolean
  primaryFolderEntryPoint?: FolderShareEntryPoint | null
  createdAtUtc: string
  expiresAtUtc?: string | null
  maxUses?: number | null
  useCount: number
  state: string
  publishMode: PublishMode
  brokenReason?: string | null
  lastAccessedAtUtc?: string | null
}

type RuntimeSnapshot = {
  shares: ShareRecord[]
  transfers: TransferRecord[]
  settings: Record<string, unknown>
  cloudflared: Record<string, unknown>
}

type TransferRecord = {
  id: string
  shareId: string
  token: string
  fileName: string
  transferKind?: 'FileDownload' | 'FolderZipDownload' | 'FolderFileDownload' | 'MetadataPreview' | null
  requesterName?: string | null
  remoteAddress?: string | null
  bytesSent: number
  totalBytes: number
  startedAtUtc: string
  lastUpdatedAtUtc: string
  completedAtUtc?: string | null
  state: 'InProgress' | 'Paused' | 'Completed' | 'Failed'
  isActive: boolean
  succeeded: boolean
  error?: string | null
}

type RuntimeEvent = {
  type: string
  occurredAtUtc: string
  payload: Record<string, unknown>
}

type CloudflareManagedStatus = {
  loggedIn: boolean
  message: string
  accountId?: string | null
  zoneId?: string | null
  zoneName?: string | null
  domains?: Array<{ zoneId: string; name: string }> | null
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

type CloudflaredDashboardStatus = {
  installed: boolean
  executablePath?: string | null
  installedVersion?: string | null
  latestVersion?: string | null
  updateAvailable: boolean
  ownership: string
  loggedIn: boolean
  loginMessage: string
}

const agentBaseUrl = 'http://127.0.0.1:46430'

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

contextBridge.exposeInMainWorld('instantFileShare', {
  getAppVersion: () => ipcRenderer.invoke('app:getVersion') as Promise<string>,
  getRuntime: () => request<RuntimeSnapshot>('/api/runtime'),
  getShares: () => request<ShareRecord[]>('/api/shares'),
  getTransfers: () => request<TransferRecord[]>('/api/transfers'),
  removeTransfer: (transferId: string) =>
    request<void>(`/api/transfers/${transferId}`, { method: 'DELETE' }),
  clearTransferHistory: () =>
    request<void>('/api/transfers', { method: 'DELETE' }),
  createShare: (filePath: string, publishMode?: PublishMode) =>
    request<{ share: ShareRecord; url: string }>('/api/shares', {
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
  savePublishProfile: (mode: PublishMode, profile: Record<string, unknown>) =>
    request<void>(`/api/publish-profiles/${mode}`, {
      method: 'PUT',
      body: JSON.stringify(profile),
    }),
  detectCloudflared: () => request<Record<string, unknown>>('/api/cloudflared/detect', { method: 'POST' }),
  installCloudflared: () => request<Record<string, unknown>>('/api/cloudflared/install', { method: 'POST' }),
  updateCloudflared: () => request<Record<string, unknown>>('/api/cloudflared/update', { method: 'POST' }),
  startCloudflareLogin: () => request<Record<string, unknown>>('/api/cloudflared/login', { method: 'POST' }),
  logoutCloudflare: () => request<Record<string, unknown>>('/api/cloudflared/logout', { method: 'POST' }),
  getCloudflaredStatus: () => request<CloudflaredDashboardStatus>('/api/cloudflared/status'),
  pickCloudflaredExecutable: () => ipcRenderer.invoke('cloudflared:pickExecutable') as Promise<string | null>,
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
  getAgentLogs: async () => {
    const response = await fetch(`${agentBaseUrl}/api/logs/agent`)
    return response.text()
  },
  getCloudflareLogs: async () => {
    const response = await fetch(`${agentBaseUrl}/api/logs/cloudflare`)
    return response.text()
  },
  connectRuntime: (onMessage: (event: RuntimeEvent) => void) => {
    const socket = new WebSocket('ws://127.0.0.1:46430/ws/runtime')
    socket.addEventListener('message', (event) => {
      onMessage(JSON.parse(event.data as string) as RuntimeEvent)
    })
    return () => socket.close()
  },
})
