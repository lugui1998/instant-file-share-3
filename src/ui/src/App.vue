<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import AppSidebar from './components/AppSidebar.vue'
import ConfirmModal from './components/ConfirmModal.vue'
import LogsView from './components/views/LogsView.vue'
import SettingsView from './components/views/SettingsView.vue'
import SharesView from './components/views/SharesView.vue'
import TransfersView from './components/views/TransfersView.vue'
import {
  agentBridge,
  type AgentRuntimeSnapshot,
  type AgentShareRecord,
  type AppSettings,
  type CloudflaredDashboardStatus,
  type CloudflareManagedAvailability,
  type CloudflareManagedStatus,
  type PublishMode,
  type RuntimeEvent,
  type TransferRecord,
} from './agentBridge'
import type { BandwidthUnit, ViewKey } from './types/ui'

const activeView = ref<ViewKey>('shares')
const runtime = ref<AgentRuntimeSnapshot | null>(null)
const pending = ref(false)
const error = ref<string | null>(null)
const draftFilePath = ref('')
const draftMode = ref<PublishMode>('QuickTunnel')
const cloudflaredStatus = ref<CloudflaredDashboardStatus | null>(null)
const managedStatus = ref<CloudflareManagedStatus | null>(null)
const managedAvailability = ref<CloudflareManagedAvailability | null>(null)
const agentLogs = ref('')
const cloudflareLogs = ref('')
const selectedDomain = ref('')
const managedSubdomain = ref('share')
const settingsSaveMessage = ref('')
const bandwidthValue = ref<number | null>(null)
const bandwidthUnit = ref<BandwidthUnit>('KB/s')
const isHydratingSettings = ref(false)
const isSavingSettings = ref(false)
const isHydratingManagedSelection = ref(false)
const isLoadingCloudflareSettings = ref(false)
const lastSavedSettingsSignature = ref('')
const lastManagedAvailabilityKey = ref('')
const settingsDraft = ref<AppSettings>({
  defaultPublishMode: 'QuickTunnel',
  publicTokenLength: 11,
  defaultExpiryValue: 24,
  defaultExpiryUnit: 'Hours',
  defaultMaxUses: null,
  defaultReceiveExpiryValue: 24,
  defaultReceiveExpiryUnit: 'Hours',
  defaultReceiveMaxTotalBytes: 10 * 1024 * 1024 * 1024,
  friendlyUrlsEnabled: true,
  sendMetadataToCrawlers: true,
  openImagesInBrowser: true,
  openVideosInBrowser: true,
  openPdfInBrowser: true,
  fileChangeBehavior: 'Strict',
  keepAwakeWhileTransferring: true,
  bandwidthLimitBytesPerSecond: null,
  cloudflaredPathOverride: '',
  startOnLogin: true,
  openDashboardOnStart: false,
  manualBindAddress: '127.0.0.1',
  manualPublicPort: 46431,
  manualBaseUrl: '',
  localApiPort: 46430,
  showLogs: false,
  addFileContextMenuButton: true,
  addFolderZipContextMenuButton: true,
  addFolderBrowseContextMenuButton: true,
  addFolderReceiveContextMenuButton: true,
  receiveNotificationsEnabled: true,
  folderShareCapabilityPolicy: 'Exclusive',
  folderZipCompressionLevel: 'Optimal',
  historyRetentionValue: 3,
  historyRetentionUnit: 'Months',
  historyItemsPerPage: 25,
  sharesItemsPerPage: 25,
})

let disconnect: () => void = () => {}
let managedAvailabilityTimer: ReturnType<typeof setTimeout> | null = null
let settingsSaveTimer: ReturnType<typeof setTimeout> | null = null
let transferPollingTimer: ReturnType<typeof setInterval> | null = null
let runtimeRetryTimer: ReturnType<typeof setTimeout> | null = null
const removingTransferId = ref<string | null>(null)
const isClearingTransferHistory = ref(false)
const showClearHistoryModal = ref(false)

const transfers = computed(() => runtime.value?.transfers ?? [])
const currentSettingsSignature = computed(() => JSON.stringify(buildSettingsPayload()))
const currentManagedAvailabilityKey = computed(() => {
  const domain = selectedDomain.value.trim()
  const subdomain = managedSubdomain.value.trim()
  return managedStatus.value?.loggedIn && domain && subdomain ? `${domain}|${subdomain}` : ''
})

const viewLoaders: Partial<Record<ViewKey, () => Promise<void>>> = {
  settings: loadCloudflareSettings,
  logs: loadAgentLogs,
  cloudflareLogs: loadCloudflareLogs,
}

function getErrorMessage(cause: unknown, fallback: string) {
  return cause instanceof Error ? cause.message : fallback
}

function ensureRuntimeState() {
  if (!runtime.value) {
    runtime.value = {
      shares: [],
      transfers: [],
      settings: { ...settingsDraft.value },
      cloudflared: {},
    }
  }

  return runtime.value
}

async function runAction(action: () => Promise<void>, fallback: string) {
  error.value = null

  try {
    await action()
  } catch (cause) {
    error.value = getErrorMessage(cause, fallback)
  }
}

async function loadRuntime() {
  pending.value = true
  error.value = null

  try {
    const runtimeSnapshot = await agentBridge.getRuntime()
    runtime.value = runtimeSnapshot
    draftMode.value = runtimeSnapshot.settings.defaultPublishMode
    isHydratingSettings.value = true
    Object.assign(settingsDraft.value, runtimeSnapshot.settings)
    const bandwidth = parseBandwidthLimit(runtimeSnapshot.settings.bandwidthLimitBytesPerSecond ?? null)
    bandwidthValue.value = bandwidth.value
    bandwidthUnit.value = bandwidth.unit
    lastSavedSettingsSignature.value = JSON.stringify(buildSettingsPayload())
    if (runtimeRetryTimer) {
      clearTimeout(runtimeRetryTimer)
      runtimeRetryTimer = null
    }
  } catch (cause) {
    error.value = getErrorMessage(cause, 'Failed to contact the local agent.')
    if (!runtimeRetryTimer) {
      runtimeRetryTimer = setTimeout(() => {
        runtimeRetryTimer = null
        void loadRuntime()
      }, 2000)
    }
  } finally {
    await nextTick()
    isHydratingSettings.value = false
    pending.value = false
  }
}

async function loadTransfers(silent = false) {
  try {
    const latestTransfers = await agentBridge.getTransfers()
    ensureRuntimeState().transfers = latestTransfers
  } catch (cause) {
    if (!silent) {
      error.value = getErrorMessage(cause, 'Failed to load transfers.')
    }
  }
}

async function loadCloudflareSettings() {
  if (activeView.value !== 'settings' || isLoadingCloudflareSettings.value) {
    return
  }

  isLoadingCloudflareSettings.value = true

  try {
    cloudflaredStatus.value = await agentBridge.getCloudflaredStatus()
    managedStatus.value = await agentBridge.getManagedCloudflareStatus()
    const domains = managedStatus.value.domains ?? []
    const configuredHostname = managedStatus.value.configuredHostname ?? ''

    isHydratingManagedSelection.value = true

    if (configuredHostname.includes('.')) {
      const hostnameParts = configuredHostname.split('.')
      managedSubdomain.value = hostnameParts.shift() ?? managedSubdomain.value
      selectedDomain.value = hostnameParts.join('.')
    } else if (!selectedDomain.value && domains.length > 0) {
      selectedDomain.value = domains[0].name
    }

    await nextTick()
    isHydratingManagedSelection.value = false
    await refreshManagedAvailability()
  } catch {
    cloudflaredStatus.value = null
    managedStatus.value = null
    managedAvailability.value = null
    isHydratingManagedSelection.value = false
  } finally {
    isLoadingCloudflareSettings.value = false
  }
}

async function createShare() {
  error.value = null

  if (!draftFilePath.value.trim()) {
    error.value = 'Enter a local file path to create a test share from the dashboard.'
    return
  }

  pending.value = true

  try {
    await agentBridge.createShare(draftFilePath.value.trim(), draftMode.value)
    draftFilePath.value = ''
    await loadRuntime()
  } catch (cause) {
    error.value = getErrorMessage(cause, 'Share creation failed.')
  } finally {
    pending.value = false
  }
}

async function revokeShare(shareId: string) {
  await runAction(async () => {
    await agentBridge.revokeShare(shareId)
    await loadRuntime()
  }, 'Failed to revoke the share.')
}

function buildShareUrl(share: AgentShareRecord) {
  const baseUrl = share.publicBaseUrl.replace(/\/$/, '')
  if (share.shareKind === 'Folder') {
    const folderSlug = share.slug ?? share.fileName.trim().toLowerCase().replace(/\s+/g, '-')
    if (share.primaryFolderEntryPoint === 'Zip') {
      return `${baseUrl}/s/${share.token}/${encodeURIComponent(folderSlug)}.zip`
    }

    return `${baseUrl}/s/${share.token}/${encodeURIComponent(folderSlug)}`
  }

  const extension = share.fileName.includes('.') ? share.fileName.slice(share.fileName.lastIndexOf('.')) : ''
  const friendlySegment = share.slug
    ? share.slug.toLowerCase().endsWith(extension.toLowerCase())
      ? share.slug
      : `${share.slug}${extension}`
    : null

  return friendlySegment
    ? `${baseUrl}/s/${share.token}/${encodeURIComponent(friendlySegment)}`
    : `${baseUrl}/s/${share.token}`
}

function sortTransfers(transfers: TransferRecord[]) {
  return [...transfers].sort((left, right) => {
    if (left.isActive !== right.isActive) {
      return left.isActive ? -1 : 1
    }

    return new Date(right.startedAtUtc).getTime() - new Date(left.startedAtUtc).getTime()
  })
}

function upsertTransfer(transfer: TransferRecord) {
  const runtimeState = ensureRuntimeState()
  const existingIndex = runtimeState.transfers.findIndex((entry) => entry.id === transfer.id)

  if (existingIndex >= 0) {
    runtimeState.transfers.splice(existingIndex, 1, transfer)
  } else {
    runtimeState.transfers.unshift(transfer)
  }

  runtimeState.transfers = sortTransfers(runtimeState.transfers)
}

function removeTransferFromState(transferId: string) {
  const runtimeState = ensureRuntimeState()
  runtimeState.transfers = sortTransfers(runtimeState.transfers.filter((entry) => entry.id !== transferId))
}

function clearTransferHistoryFromState() {
  const runtimeState = ensureRuntimeState()
  runtimeState.transfers = sortTransfers(runtimeState.transfers.filter((entry) => entry.isActive))
}

async function removeTransfer(transferId: string) {
  if (removingTransferId.value || isClearingTransferHistory.value) {
    return
  }

  error.value = null
  removingTransferId.value = transferId

  try {
    await agentBridge.removeTransfer(transferId)
    removeTransferFromState(transferId)
  } catch (cause) {
    error.value = getErrorMessage(cause, 'Failed to remove history entry.')
  } finally {
    removingTransferId.value = null
  }
}

async function clearTransferHistory() {
  if (removingTransferId.value || isClearingTransferHistory.value) {
    return
  }

  error.value = null
  isClearingTransferHistory.value = true

  try {
    await agentBridge.clearTransferHistory()
    clearTransferHistoryFromState()
    showClearHistoryModal.value = false
  } catch (cause) {
    error.value = getErrorMessage(cause, 'Failed to clear history.')
  } finally {
    isClearingTransferHistory.value = false
  }
}

function openClearHistoryModal() {
  if (isClearingTransferHistory.value) {
    return
  }

  showClearHistoryModal.value = true
}

function closeClearHistoryModal() {
  if (isClearingTransferHistory.value) {
    return
  }

  showClearHistoryModal.value = false
}

function applyRuntimeEvent(event: RuntimeEvent) {
  switch (event.type) {
    case 'TransferStarted':
    case 'TransferProgress':
    case 'TransferPaused':
    case 'TransferCompleted':
    case 'TransferFailed':
      upsertTransfer(event.payload as unknown as TransferRecord)
      break
    case 'ShareCreated':
    case 'ShareUpdated':
    case 'ShareRevoked':
    case 'SettingsUpdated':
    case 'CloudflaredUpdated':
      void loadRuntime()
      break
    case 'TransferRemoved':
      if (typeof event.payload.transferId === 'string') {
        removeTransferFromState(event.payload.transferId)
      }
      break
    case 'TransferHistoryCleared':
      clearTransferHistoryFromState()
      break
    default:
      break
  }
}

function startTransferPolling() {
  if (transferPollingTimer) {
    return
  }

  transferPollingTimer = setInterval(() => {
    void loadTransfers(true)
  }, 500)
}

function stopTransferPolling() {
  if (transferPollingTimer) {
    clearInterval(transferPollingTimer)
    transferPollingTimer = null
  }
}

async function copyShareLink(share: AgentShareRecord) {
  await runAction(async () => {
    await navigator.clipboard.writeText(buildShareUrl(share))
  }, 'Failed to copy the share link.')
}

async function installCloudflared() {
  await runAction(async () => {
    await agentBridge.installCloudflared()
    await loadCloudflareSettings()
  }, 'Failed to install cloudflared.')
}

async function updateCloudflared() {
  await runAction(async () => {
    await agentBridge.updateCloudflared()
    await loadCloudflareSettings()
  }, 'Failed to update cloudflared.')
}

async function toggleCloudflareLogin() {
  await runAction(async () => {
    if (cloudflaredStatus.value?.loggedIn) {
      await agentBridge.logoutCloudflare()
    } else {
      await agentBridge.startCloudflareLogin()
    }

    await loadCloudflareSettings()
  }, cloudflaredStatus.value?.loggedIn
    ? 'Failed to log out from Cloudflare on this device.'
    : 'Failed to start the Cloudflare login flow.')
}

async function openCloudflaredPathPicker() {
  await runAction(async () => {
    const pickedPath = await agentBridge.pickCloudflaredExecutable()

    if (pickedPath) {
      settingsDraft.value.cloudflaredPathOverride = pickedPath
    }
  }, 'Failed to select the cloudflared executable.')
}

async function createManagedTunnel() {
  error.value = null

  const domain = selectedDomain.value.trim()
  const subdomain = managedSubdomain.value.trim()

  if (!domain) {
    error.value = 'Select a domain first.'
    return
  }

  if (!subdomain) {
    error.value = 'Enter the subdomain you want to use for file sharing.'
    return
  }

  await runAction(async () => {
    await agentBridge.createManagedTunnel(domain, subdomain)
    await loadCloudflareSettings()
  }, 'Failed to create the managed tunnel.')
}

async function loadAgentLogs() {
  await runAction(async () => {
    agentLogs.value = await agentBridge.getAgentLogs()
  }, 'Failed to load the agent logs.')
}

async function loadCloudflareLogs() {
  await runAction(async () => {
    cloudflareLogs.value = await agentBridge.getCloudflareLogs()
  }, 'Failed to load the Cloudflare logs.')
}

async function saveSettings() {
  if (isSavingSettings.value) {
    return
  }

  try {
    error.value = null
    isSavingSettings.value = true
    settingsSaveMessage.value = ''
    const nextSettings = buildSettingsPayload()

    await agentBridge.saveSettings(nextSettings)

    if (runtime.value) {
      runtime.value.settings = nextSettings
    }

    draftMode.value = settingsDraft.value.defaultPublishMode
    lastSavedSettingsSignature.value = JSON.stringify(nextSettings)
    settingsSaveMessage.value = 'Settings saved.'
  } catch (cause) {
    error.value = getErrorMessage(cause, 'Failed to save settings.')
  } finally {
    isSavingSettings.value = false
  }
}

async function refreshManagedAvailability() {
  const key = currentManagedAvailabilityKey.value
  const domain = selectedDomain.value.trim()
  const subdomain = managedSubdomain.value.trim()

  if (!managedStatus.value?.loggedIn || !domain || !subdomain) {
    managedAvailability.value = null
    lastManagedAvailabilityKey.value = ''
    return
  }

  managedAvailability.value = await agentBridge.checkManagedTunnelAvailability(domain, subdomain)
  lastManagedAvailabilityKey.value = key
}

function parseBandwidthLimit(value: number | null) {
  if (value === null || value <= 0) {
    return { value: null, unit: 'KB/s' as BandwidthUnit }
  }

  if (value % (1024 * 1024) === 0 && value >= 1024 * 1024) {
    return { value: value / (1024 * 1024), unit: 'MB/s' as BandwidthUnit }
  }

  if (value % 1024 === 0 && value >= 1024) {
    return { value: value / 1024, unit: 'KB/s' as BandwidthUnit }
  }

  return { value, unit: 'B/s' as BandwidthUnit }
}

function serializeBandwidthLimit(value: number | null, unit: BandwidthUnit) {
  if (value === null || value <= 0) {
    return null
  }

  switch (unit) {
    case 'MB/s':
      return value * 1024 * 1024
    case 'KB/s':
      return value * 1024
    default:
      return value
  }
}

function normalizeWholeNumber(value: number | null | undefined, fallback: number, minimum = 0, maximum?: number) {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return fallback
  }

  const normalized = Math.max(minimum, Math.round(value))
  return typeof maximum === 'number' ? Math.min(maximum, normalized) : normalized
}

function buildSettingsPayload(): AppSettings {
  return {
    ...settingsDraft.value,
    publicTokenLength: normalizeWholeNumber(settingsDraft.value.publicTokenLength, 11, 6, 128),
    cloudflaredPathOverride: settingsDraft.value.cloudflaredPathOverride || null,
    manualBaseUrl: settingsDraft.value.manualBaseUrl || null,
    defaultMaxUses: settingsDraft.value.defaultMaxUses ?? null,
    defaultReceiveExpiryValue: normalizeWholeNumber(settingsDraft.value.defaultReceiveExpiryValue, 24, 0),
    defaultReceiveMaxTotalBytes: normalizeWholeNumber(settingsDraft.value.defaultReceiveMaxTotalBytes, 10 * 1024 * 1024 * 1024, 0),
    historyRetentionValue: normalizeWholeNumber(settingsDraft.value.historyRetentionValue, 3, 0),
    historyRetentionUnit: settingsDraft.value.historyRetentionUnit,
    historyItemsPerPage: normalizeWholeNumber(settingsDraft.value.historyItemsPerPage, 25, 0),
    sharesItemsPerPage: normalizeWholeNumber(settingsDraft.value.sharesItemsPerPage, 25, 0),
    bandwidthLimitBytesPerSecond: serializeBandwidthLimit(bandwidthValue.value, bandwidthUnit.value),
  }
}

onMounted(async () => {
  disconnect = agentBridge.connectRuntime((event) => {
    applyRuntimeEvent(event)
  })
  await loadRuntime()
})

onUnmounted(() => {
  if (managedAvailabilityTimer) {
    clearTimeout(managedAvailabilityTimer)
  }

  if (settingsSaveTimer) {
    clearTimeout(settingsSaveTimer)
  }

  if (runtimeRetryTimer) {
    clearTimeout(runtimeRetryTimer)
  }

  stopTransferPolling()

  disconnect()
})

watch(currentManagedAvailabilityKey, (nextKey) => {
  if (
    activeView.value !== 'settings' ||
    isHydratingManagedSelection.value ||
    !nextKey ||
    nextKey === lastManagedAvailabilityKey.value
  ) {
    return
  }

  if (managedAvailabilityTimer) {
    clearTimeout(managedAvailabilityTimer)
  }

  managedAvailabilityTimer = setTimeout(async () => {
    try {
      await refreshManagedAvailability()
    } catch {
      managedAvailability.value = null
    }
  }, 250)
})

watch(activeView, (nextView) => {
  if (nextView === 'transfers') {
    void loadTransfers()
    startTransferPolling()
  } else {
    stopTransferPolling()
  }

  const loader = viewLoaders[nextView]

  if (loader) {
    void loader()
  }
})

watch(
  () => settingsDraft.value.showLogs,
  (showLogs) => {
    if (!showLogs && (activeView.value === 'logs' || activeView.value === 'cloudflareLogs')) {
      activeView.value = 'shares'
    }
  },
)

watch(
  [settingsDraft, bandwidthValue, bandwidthUnit],
  () => {
    const nextSignature = currentSettingsSignature.value

    if (
      !runtime.value ||
      isHydratingSettings.value ||
      isSavingSettings.value ||
      nextSignature === lastSavedSettingsSignature.value
    ) {
      return
    }

    if (settingsSaveTimer) {
      clearTimeout(settingsSaveTimer)
    }

    settingsSaveTimer = setTimeout(async () => {
      try {
        await saveSettings()
      } catch {
      }
    }, 400)
  },
  { deep: true },
)
</script>

<template>
  <div class="shell">
    <AppSidebar
      :active-view="activeView"
      :show-logs="settingsDraft.showLogs"
      @navigate="activeView = $event"
    />

    <main class="content">
      <p v-if="error" class="error-banner">{{ error }}</p>

      <SharesView
        v-if="activeView === 'shares'"
        v-model:draft-file-path="draftFilePath"
        v-model:draft-mode="draftMode"
        :pending="pending"
        :shares="runtime?.shares ?? []"
        :items-per-page="settingsDraft.sharesItemsPerPage"
        @copy-share="copyShareLink"
        @create-share="createShare"
        @revoke-share="revokeShare"
      />

      <TransfersView
        v-else-if="activeView === 'transfers'"
        :transfers="transfers"
        :items-per-page="settingsDraft.historyItemsPerPage"
        :removing-transfer-id="removingTransferId"
        :clearing-history="isClearingTransferHistory"
        @remove-transfer="removeTransfer"
      />

      <SettingsView
        v-else-if="activeView === 'settings'"
        v-model:settings-draft="settingsDraft"
        v-model:bandwidth-value="bandwidthValue"
        v-model:bandwidth-unit="bandwidthUnit"
        v-model:selected-domain="selectedDomain"
        v-model:managed-subdomain="managedSubdomain"
        :cloudflared-status="cloudflaredStatus"
        :clearing-transfer-history="isClearingTransferHistory"
        :managed-status="managedStatus"
        :managed-availability="managedAvailability"
        :save-message="settingsSaveMessage"
        @clear-transfer-history="openClearHistoryModal"
        @create-managed-tunnel="createManagedTunnel"
        @install-cloudflared="installCloudflared"
        @pick-cloudflared-path="openCloudflaredPathPicker"
        @toggle-cloudflare-login="toggleCloudflareLogin"
        @update-cloudflared="updateCloudflared"
      />

      <LogsView
        v-else-if="activeView === 'logs'"
        eyebrow="Logs"
        title="Agent logs"
        :logs="agentLogs"
        @refresh="loadAgentLogs"
      />

      <LogsView
        v-else-if="activeView === 'cloudflareLogs'"
        eyebrow="Cloudflare logs"
        title="cloudflared logs"
        :logs="cloudflareLogs"
        @refresh="loadCloudflareLogs"
      />
    </main>

    <ConfirmModal
      v-if="showClearHistoryModal"
      cancel-label="Keep history"
      confirm-label="Clear history"
      message="This removes completed, paused, and failed history entries. Active transfers stay visible."
      :pending="isClearingTransferHistory"
      title="Clear transfer history?"
      @cancel="closeClearHistoryModal"
      @confirm="clearTransferHistory"
    />
  </div>
</template>
