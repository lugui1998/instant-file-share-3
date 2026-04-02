import packageMetadata from '../package.json'

type AgentShareRecord = {
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
  publishMode: string
  brokenReason?: string | null
  lastAccessedAtUtc?: string | null
}

type AgentRuntimeSnapshot = {
  shares: AgentShareRecord[]
  transfers: TransferRecord[]
  settings: AppSettings
  cloudflared: Record<string, unknown>
}

type PublishMode = 'QuickTunnel' | 'ManagedCloudflare' | 'Manual'
type ShareKind = 'File' | 'Folder'
type FolderShareEntryPoint = 'Browse' | 'Zip'
type FolderShareCapabilityPolicy = 'Exclusive' | 'AllowBoth'
type FolderZipCompressionLevel = 'Optimal' | 'Fastest' | 'NoCompression' | 'SmallestSize'
type FileChangeBehavior = 'Strict' | 'Lenient'
type ExpiryUnit = 'Minutes' | 'Hours' | 'Days'
type HistoryRetentionUnit = 'Minutes' | 'Hours' | 'Days' | 'Months' | 'Years'
type TransferKind = 'FileDownload' | 'FolderZipDownload' | 'FolderFileDownload' | 'MetadataPreview' | 'FileUpload'
type TransferRecord = Record<string, unknown> & {
  id: string
  shareId: string
  token: string
  fileName?: string | null
  transferKind?: TransferKind | null
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

type AppSettings = {
  defaultPublishMode: PublishMode
  publicTokenLength: number
  defaultExpiryValue: number
  defaultExpiryUnit: ExpiryUnit
  defaultMaxUses?: number | null
  defaultReceiveExpiryValue: number
  defaultReceiveExpiryUnit: ExpiryUnit
  defaultReceiveMaxTotalBytes: number
  friendlyUrlsEnabled: boolean
  sendMetadataToCrawlers: boolean
  openImagesInBrowser: boolean
  openVideosInBrowser: boolean
  openPdfInBrowser: boolean
  fileChangeBehavior: FileChangeBehavior
  keepAwakeWhileTransferring: boolean
  bandwidthLimitBytesPerSecond?: number | null
  cloudflaredPathOverride?: string | null
  startOnLogin: boolean
  openDashboardOnStart: boolean
  manualBindAddress: string
  manualPublicPort: number
  manualBaseUrl?: string | null
  localApiPort: number
  showLogs: boolean
  addFileContextMenuButton: boolean
  addFolderZipContextMenuButton: boolean
  addFolderBrowseContextMenuButton: boolean
  addFolderReceiveContextMenuButton: boolean
  receiveNotificationsEnabled: boolean
  folderShareCapabilityPolicy: FolderShareCapabilityPolicy
  folderZipCompressionLevel: FolderZipCompressionLevel
  historyRetentionValue: number
  historyRetentionUnit: HistoryRetentionUnit
  historyItemsPerPage: number
  sharesItemsPerPage: number
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

type AgentBridge = {
  getAppVersion(): Promise<string>
  getRuntime(): Promise<AgentRuntimeSnapshot>
  getShares(): Promise<AgentShareRecord[]>
  getTransfers(): Promise<TransferRecord[]>
  removeTransfer(transferId: string): Promise<void>
  clearTransferHistory(): Promise<void>
  createShare(filePath: string, publishMode?: string): Promise<{ share: AgentShareRecord; url: string }>
  revokeShare(shareId: string): Promise<void>
  getSettings(): Promise<Record<string, unknown>>
  saveSettings(settings: Record<string, unknown>): Promise<void>
  saveSettings(settings: AppSettings): Promise<void>
  getPublishProfiles(): Promise<Array<Record<string, unknown>>>
  savePublishProfile(mode: string, profile: Record<string, unknown>): Promise<void>
  detectCloudflared(): Promise<Record<string, unknown>>
  installCloudflared(): Promise<Record<string, unknown>>
  updateCloudflared(): Promise<Record<string, unknown>>
  startCloudflareLogin(): Promise<Record<string, unknown>>
  logoutCloudflare(): Promise<Record<string, unknown>>
  getCloudflaredStatus(): Promise<CloudflaredDashboardStatus>
  pickCloudflaredExecutable(): Promise<string | null>
  getManagedCloudflareStatus(): Promise<CloudflareManagedStatus>
  checkManagedTunnelAvailability(domain: string, subdomain: string): Promise<CloudflareManagedAvailability>
  createManagedTunnel(domain: string, subdomain: string): Promise<Record<string, unknown>>
  getAgentLogs(): Promise<string>
  getCloudflareLogs(): Promise<string>
  connectRuntime(onMessage: (event: RuntimeEvent) => void): () => void
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
    getAppVersion: async () => packageMetadata.version,
    getRuntime: () => request<AgentRuntimeSnapshot>('/api/runtime'),
    getShares: () => request<AgentShareRecord[]>('/api/shares'),
    getTransfers: () => request<TransferRecord[]>('/api/transfers'),
    removeTransfer: (transferId: string) =>
      request<void>(`/api/transfers/${transferId}`, { method: 'DELETE' }),
    clearTransferHistory: () =>
      request<void>('/api/transfers', { method: 'DELETE' }),
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
    logoutCloudflare: () => request<Record<string, unknown>>('/api/cloudflared/logout', { method: 'POST' }),
    getCloudflaredStatus: () => request<CloudflaredDashboardStatus>('/api/cloudflared/status'),
    pickCloudflaredExecutable: async () => null,
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
      let socket: WebSocket | null = null
      let reconnectTimer: ReturnType<typeof setTimeout> | null = null
      let disposed = false

      const connect = () => {
        if (disposed) {
          return
        }

        socket = new WebSocket(runtimeSocketUrl)
        socket.addEventListener('message', (event) => {
          onMessage(JSON.parse(event.data as string) as RuntimeEvent)
        })
        socket.addEventListener('error', () => {
          socket?.close()
        })
        socket.addEventListener('close', () => {
          socket = null
          if (disposed || reconnectTimer) {
            return
          }

          reconnectTimer = setTimeout(() => {
            reconnectTimer = null
            connect()
          }, 2000)
        })
      }

      connect()

      return () => {
        disposed = true
        if (reconnectTimer) {
          clearTimeout(reconnectTimer)
          reconnectTimer = null
        }
        socket?.close()
      }
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
  AppSettings,
  CloudflaredDashboardStatus,
  CloudflareManagedAvailability,
  CloudflareManagedStatus,
  PublishMode,
  ExpiryUnit,
  RuntimeEvent,
  TransferRecord,
}
