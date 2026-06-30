<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type {
  AppSettings,
  CloudflaredDashboardStatus,
  CloudflareManagedAvailability,
  CloudflareManagedStatus,
} from '../../agentBridge'
import type { BandwidthUnit } from '../../types/ui'
import HelpTooltip from '../settings/HelpTooltip.vue'
import SettingCard from '../settings/SettingCard.vue'
import ToggleField from '../settings/ToggleField.vue'

const props = defineProps<{
  cloudflaredStatus: CloudflaredDashboardStatus | null
  clearingTransferHistory: boolean
  managedStatus: CloudflareManagedStatus | null
  managedAvailability: CloudflareManagedAvailability | null
  saveMessage: string
}>()

const settingsDraft = defineModel<AppSettings>('settingsDraft', { required: true })
const bandwidthValue = defineModel<number | null>('bandwidthValue', { required: true })
const bandwidthUnit = defineModel<BandwidthUnit>('bandwidthUnit', { required: true })
const selectedDomain = defineModel<string>('selectedDomain', { required: true })
const managedSubdomain = defineModel<string>('managedSubdomain', { required: true })

const emit = defineEmits<{
  clearTransferHistory: []
  createManagedTunnel: []
  installCloudflared: []
  pickCloudflaredPath: []
  toggleCloudflareLogin: []
  updateCloudflared: []
}>()

const availableDomains = computed(() => props.managedStatus?.domains ?? [])
const cloudflareLoginLabel = computed(() =>
  props.cloudflaredStatus?.loggedIn ? 'Log out' : 'Start Cloudflare login',
)
const isShortPublicTokenLength = computed(() => settingsDraft.value.publicTokenLength < 11)
const bytesPerMegabyte = 1024 * 1024
const receiveMaxTotalUnit = ref<'Unlimited' | 'MB' | 'GB' | 'TB'>(resolveReceiveMaxTotalUnit(settingsDraft.value.defaultReceiveMaxTotalBytes))
const receiveMaxTotalValue = computed({
  get: () => {
    if (receiveMaxTotalUnit.value === 'Unlimited') {
      return 0
    }

    return Math.max(1, Math.round(settingsDraft.value.defaultReceiveMaxTotalBytes / getReceiveMaxTotalMultiplier(receiveMaxTotalUnit.value)))
  },
  set: (value: number) => {
    if (receiveMaxTotalUnit.value === 'Unlimited') {
      settingsDraft.value.defaultReceiveMaxTotalBytes = 0
      return
    }

    const normalized = Number.isFinite(value) ? Math.max(1, Math.round(value)) : 10
    settingsDraft.value.defaultReceiveMaxTotalBytes = normalized * getReceiveMaxTotalMultiplier(receiveMaxTotalUnit.value)
  },
})
const showUnlimitedReceiveQuotaWarning = computed(() => settingsDraft.value.defaultReceiveMaxTotalBytes === 0)
const cloudflareZonePlanKey = computed(() =>
  (props.managedStatus?.zonePlanLegacyId || props.managedStatus?.zonePlanName || '').trim().toLowerCase(),
)
const showCloudflareFreePlanBodySizeWarning = computed(() =>
  settingsDraft.value.defaultPublishMode !== 'Manual' &&
  cloudflareZonePlanKey.value === 'free' &&
  receiveUploadMaxBodySizeMb.value > 100,
)
const showCloudflareTargetTimeoutWarning = computed(() =>
  settingsDraft.value.defaultPublishMode !== 'Manual' &&
  settingsDraft.value.receiveUploadChunkSizingMode === 'Auto' &&
  settingsDraft.value.receiveUploadChunkTargetSeconds > 120,
)
const receiveUploadChunkSizeMb = computed({
  get: () => Math.max(1, Math.round((settingsDraft.value.receiveUploadChunkSizeBytes ?? 16 * bytesPerMegabyte) / bytesPerMegabyte)),
  set: (value: number) => {
    const normalized = Number.isFinite(value) ? Math.max(1, Math.round(value)) : 16
    settingsDraft.value.receiveUploadChunkSizeBytes = normalized * bytesPerMegabyte
  },
})
const receiveUploadMaxBodySizeMb = computed({
  get: () => Math.max(1, Math.round((settingsDraft.value.receiveUploadMaxBodySizeBytes ?? 95 * bytesPerMegabyte) / bytesPerMegabyte)),
  set: (value: number) => {
    const normalized = Number.isFinite(value) ? Math.max(1, Math.round(value)) : 95
    settingsDraft.value.receiveUploadMaxBodySizeBytes = normalized * bytesPerMegabyte
  },
})
const browserManagedDownloadMaxMemoryMb = computed({
  get: () => Math.max(1, Math.round((settingsDraft.value.browserManagedDownloadMaxMemoryBytes ?? 512 * bytesPerMegabyte) / bytesPerMegabyte)),
  set: (value: number) => {
    const normalized = Number.isFinite(value) ? Math.max(1, Math.round(value)) : 512
    settingsDraft.value.browserManagedDownloadMaxMemoryBytes = normalized * bytesPerMegabyte
  },
})

watch(
  () => settingsDraft.value.defaultReceiveMaxTotalBytes,
  (value) => {
    receiveMaxTotalUnit.value = resolveReceiveMaxTotalUnit(value)
  },
)

watch(receiveMaxTotalUnit, (unit) => {
  if (unit === 'Unlimited') {
    settingsDraft.value.defaultReceiveMaxTotalBytes = 0
    return
  }

  if (settingsDraft.value.defaultReceiveMaxTotalBytes <= 0) {
    settingsDraft.value.defaultReceiveMaxTotalBytes = getReceiveMaxTotalMultiplier(unit)
  } else {
    settingsDraft.value.defaultReceiveMaxTotalBytes = Math.max(1, receiveMaxTotalValue.value) * getReceiveMaxTotalMultiplier(unit)
  }
})

function getReceiveMaxTotalMultiplier(unit: 'MB' | 'GB' | 'TB') {
  switch (unit) {
    case 'TB':
      return 1024 * 1024 * 1024 * 1024
    case 'GB':
      return 1024 * 1024 * 1024
    default:
      return 1024 * 1024
  }
}

function resolveReceiveMaxTotalUnit(value: number) {
  if (value <= 0) {
    return 'Unlimited'
  }

  if (value >= 1024 * 1024 * 1024 * 1024 && value % (1024 * 1024 * 1024 * 1024) === 0) {
    return 'TB'
  }

  if (value >= 1024 * 1024 * 1024 && value % (1024 * 1024 * 1024) === 0) {
    return 'GB'
  }

  return 'MB'
}
</script>

<template>
  <section class="settings-layout">
    <section class="settings-section">
      <div class="settings-section-grid settings-section-grid--sharing">
        <SettingCard class="settings-card" eyebrow="Sharing">
          <div class="field">
            <div class="field-label-row">
              <label for="default-publish-mode">Publish Mode</label>
              <HelpTooltip text="Chooses how new public links are published by default for both sending and receiving: Quick Tunnel, your own Cloudflare hostname, or a manually exposed address." />
            </div>
            <select id="default-publish-mode" v-model="settingsDraft.defaultPublishMode">
              <option value="QuickTunnel">Quick Tunnel</option>
              <option value="ManagedCloudflare">Custom Cloudflare Domain</option>
              <option value="Manual">Manual</option>
            </select>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="public-token-length">Public token length</label>
              <HelpTooltip text="Controls how many characters are used in public URLs. Shorter links are easier to guess; 11 or more characters is recommended for public sharing." />
            </div>
            <input
              id="public-token-length"
              v-model.number="settingsDraft.publicTokenLength"
              type="number"
              min="6"
              max="128"
            />
            <span class="field-help">Recommended default: 11 characters.</span>
            <span v-if="isShortPublicTokenLength" class="field-warning">
              Shorter links are easier to guess. Use 11 or more characters for public sharing.
            </span>
          </div>

          <ToggleField
            v-model="settingsDraft.keepAwakeWhileTransferring"
            input-id="keep-awake"
            label="Keep PC awake"
            help-text="Prevents the machine from sleeping while transfers are active, so long uploads and downloads do not get interrupted."
          />
          <ToggleField
            v-model="settingsDraft.startOnLogin"
            input-id="start-on-login"
            label="Start on login"
            help-text="Launches the server automatically when you sign in."
          />
          <ToggleField
            v-model="settingsDraft.openDashboardOnStart"
            input-id="open-dashboard-on-start"
            label="Open Dashboard on start"
            help-text="Opens the desktop dashboard window whenever the server starts."
          />
        </SettingCard>

        <SettingCard class="settings-card" eyebrow="History">
          <div class="field">
            <div class="field-label-row">
              <label for="history-retention-value">Keep history for</label>
              <HelpTooltip text="How long completed transfer history is kept before older entries are pruned. Use 0 for unlimited retention." />
            </div>
            <div class="input-group">
              <input
                id="history-retention-value"
                v-model.number="settingsDraft.historyRetentionValue"
                type="number"
                min="0"
                placeholder="Unlimited"
              />
              <select v-model="settingsDraft.historyRetentionUnit" class="unit-select">
                <option value="Minutes">Minutes</option>
                <option value="Hours">Hours</option>
                <option value="Days">Days</option>
                <option value="Months">Months</option>
                <option value="Years">Years</option>
              </select>
            </div>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="history-items-per-page">Items per page</label>
              <HelpTooltip text="Controls how many rows are shown per page on the History section. Use 0 to show all rows at once." />
            </div>
            <input
              id="history-items-per-page"
              v-model.number="settingsDraft.historyItemsPerPage"
              type="number"
              min="0"
              placeholder="Unlimited"
            />
          </div>

          <div class="field">
            <span class="field-help">Clear completed, paused, and failed history entries. Active transfers stay visible.</span>
          </div>

          <div class="card-actions">
            <button
              class="danger compact-button"
              type="button"
              :disabled="props.clearingTransferHistory"
              @click="emit('clearTransferHistory')"
            >
              {{ props.clearingTransferHistory ? 'Clearing...' : 'Clear history' }}
            </button>
          </div>
        </SettingCard>

        <SettingCard class="settings-card" eyebrow="Server">
          <div class="field">
            <div class="field-label-row">
              <label for="bandwidth-limit">Transfer Speed Limit</label>
              <HelpTooltip text="Caps outgoing transfer speed for each served download. Leave it empty to allow full speed." />
            </div>
            <div class="input-group">
              <input id="bandwidth-limit" v-model.number="bandwidthValue" type="number" min="0" placeholder="Unlimited" />
              <select v-model="bandwidthUnit" class="unit-select">
                <option value="B/s">B/s</option>
                <option value="KB/s">KB/s</option>
                <option value="MB/s">MB/s</option>
              </select>
            </div>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="manual-bind-address">Manual mode bind address</label>
              <HelpTooltip text="Controls which local network interface the public listener binds to when using Manual mode." />
            </div>
            <input id="manual-bind-address" v-model="settingsDraft.manualBindAddress" placeholder="127.0.0.1 or 0.0.0.0" />
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="manual-public-port">Manual mode public port</label>
              <HelpTooltip text="Port used for public share requests in Manual mode. Your router or reverse proxy must expose this port if you want outside access." />
            </div>
            <input id="manual-public-port" v-model.number="settingsDraft.manualPublicPort" type="number" min="1" max="65535" />
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="manual-base-url">Manual base URL override</label>
              <HelpTooltip text="Optional public URL to embed in generated Manual-mode links instead of auto-detecting an address." />
            </div>
            <input id="manual-base-url" v-model="settingsDraft.manualBaseUrl" placeholder="Optional https://files.example.com" />
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="local-api-port">Local API port</label>
              <HelpTooltip text="Port used by the dashboard and local integrations to talk to the server on this machine." />
            </div>
            <input id="local-api-port" v-model.number="settingsDraft.localApiPort" type="number" min="1" max="65535" />
          </div>
        </SettingCard>

      </div>
    </section>

    <section class="settings-section">
      <div class="settings-section-grid settings-section-grid--sending">
        <SettingCard class="settings-card" eyebrow="Sending">
          <div class="field">
            <div class="field-label-row">
              <label for="default-expiry-value">Expiry</label>
              <HelpTooltip text="Sets the default lifetime for new shares. Use 0 to keep shares from expiring automatically." />
            </div>
            <div class="input-group">
              <input id="default-expiry-value" v-model.number="settingsDraft.defaultExpiryValue" type="number" min="0" />
              <select v-model="settingsDraft.defaultExpiryUnit" class="unit-select">
                <option value="Minutes">Minutes</option>
                <option value="Hours">Hours</option>
                <option value="Days">Days</option>
              </select>
            </div>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="default-max-uses">Max Uses</label>
              <HelpTooltip text="Limits how many a shared file can be downloaded before the link expires. Incomplete downloads and metadata requests do not count towards this limit." />
            </div>
            <input
              id="default-max-uses"
              v-model.number="settingsDraft.defaultMaxUses"
              type="number"
              min="0"
              placeholder="Unlimited"
            />
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="file-change-behavior">If the shared file changes</label>
              <HelpTooltip text="Choose whether an existing share should stop working when the original file changes, or keep serving the latest version found at that path." />
            </div>
            <select id="file-change-behavior" v-model="settingsDraft.fileChangeBehavior">
              <option value="Strict">Stop serving file</option>
              <option value="Lenient">Serve updated file</option>
            </select>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="shares-items-per-page">Items per page</label>
              <HelpTooltip text="Controls how many rows are shown per page on the Shares section. Use 0 to disable pagination there." />
            </div>
            <input
              id="shares-items-per-page"
              v-model.number="settingsDraft.sharesItemsPerPage"
              type="number"
              min="0"
              placeholder="Unlimited"
            />
          </div>

          <ToggleField
            v-model="settingsDraft.friendlyUrlsEnabled"
            input-id="friendly-urls"
            label="Friendly URLs"
            help-text="Adds a readable filename slug after the share token in generated links."
          />
          <ToggleField
            v-model="settingsDraft.addFileContextMenuButton"
            input-id="file-context-button"
            label="File context menu: Copy share link"
            help-text="Adds a Share with Instant File Share action to the Windows file context menu."
          />
          <ToggleField
            v-model="settingsDraft.addFolderZipContextMenuButton"
            input-id="folder-zip-context-button"
            label="Folder context menu: Share as ZIP"
            help-text="Adds a folder context-menu action that creates a ZIP-style folder share."
          />
          <ToggleField
            v-model="settingsDraft.addFolderBrowseContextMenuButton"
            input-id="folder-browse-context-button"
            label="Folder context menu: Share for browsing"
            help-text="Adds a folder context-menu action that creates a browsable folder share."
          />

          <div class="field">
            <div class="field-label-row">
              <label for="folder-browse-page-title">Browse page title</label>
              <HelpTooltip text="Controls the main title shown on public folder browsing pages. Leave it blank to use this Windows user's default title." />
            </div>
            <input
              id="folder-browse-page-title"
              v-model="settingsDraft.folderBrowsePageTitle"
              placeholder="Browse files"
            />
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="folder-share-capability-policy">Folder share mode</label>
              <HelpTooltip text="Choose whether a new folder share only exposes the selected mode, or also allows the secondary ZIP/browse route." />
            </div>
            <select id="folder-share-capability-policy" v-model="settingsDraft.folderShareCapabilityPolicy">
              <option value="Exclusive">Only selected mode</option>
              <option value="AllowBoth">Allow ZIP and browsing</option>
            </select>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="folder-zip-compression-level">Folder ZIP compression</label>
              <HelpTooltip text="Controls the compression level used when streaming ZIP downloads for folder shares. More compression results in smaller files but uses more CPU and can slow down the transfer." />
            </div>
            <select id="folder-zip-compression-level" v-model="settingsDraft.folderZipCompressionLevel">
              <option value="SmallestSize">Smallest file</option>
              <option value="Optimal">Balanced</option>
              <option value="Fastest">Fast</option>
              <option value="NoCompression">No compression</option>
            </select>
          </div>
        </SettingCard>

        <SettingCard class="settings-card" eyebrow="Metadata">
          <ToggleField
            v-model="settingsDraft.sendMetadataToCrawlers"
            input-id="send-metadata-to-crawlers"
            label="Allow rich embed"
            help-text="Allows chat services and link preview crawlers to generate rich embeds instead of only showing a plain share URL."
          />
          <ToggleField
            v-model="settingsDraft.openImagesInBrowser"
            input-id="open-images-in-browser"
            label="Open Images in Browser"
            help-text="When enabled, supported image shares are served inline so browsers can display them. When disabled, they are forced to download."
          />
          <ToggleField
            v-model="settingsDraft.openVideosInBrowser"
            input-id="open-videos-in-browser"
            label="Open Videos in Browser"
            help-text="When enabled, supported video shares are served inline so browsers can try to play them. When disabled, they are forced to download."
          />
          <ToggleField
            v-model="settingsDraft.openPdfInBrowser"
            input-id="open-pdf-in-browser"
            label="Open PDF Files in Browser"
            help-text="When enabled, PDF shares are opened inline in browsers that support PDF viewing. When disabled, they are forced to download."
          />
        </SettingCard>
      </div>
    </section>

    <section class="settings-section">
      <div class="settings-section-grid settings-section-grid--receiving">
        <SettingCard class="settings-card" eyebrow="Receiving">
          <div class="field">
            <div class="field-label-row">
              <label for="receive-page-title">Upload page title</label>
              <HelpTooltip text="Controls the main title shown on public receive pages. Leave it blank to use this Windows user's default title." />
            </div>
            <input
              id="receive-page-title"
              v-model="settingsDraft.receivePageTitle"
              placeholder="Upload files"
            />
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="default-receive-expiry-value">Receive link expiry</label>
              <HelpTooltip text="Sets the default lifetime for new receive links created through the Explorer context menu. Use 0 to keep them from expiring automatically." />
            </div>
            <div class="input-group">
              <input id="default-receive-expiry-value" v-model.number="settingsDraft.defaultReceiveExpiryValue" type="number" min="0" />
              <select v-model="settingsDraft.defaultReceiveExpiryUnit" class="unit-select">
                <option value="Minutes">Minutes</option>
                <option value="Hours">Hours</option>
                <option value="Days">Days</option>
              </select>
            </div>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="default-receive-max-total-bytes">Max total upload per link</label>
              <HelpTooltip text="Caps how much data a single receive link can accept before it becomes unavailable." />
            </div>
            <div class="input-group">
              <input
                id="default-receive-max-total-bytes"
                v-model.number="receiveMaxTotalValue"
                type="number"
                min="1"
                :disabled="receiveMaxTotalUnit === 'Unlimited'"
                :placeholder="receiveMaxTotalUnit === 'Unlimited' ? 'Unlimited' : undefined"
              />
              <select v-model="receiveMaxTotalUnit" class="unit-select">
                <option value="Unlimited">Unlimited</option>
                <option value="MB">MB</option>
                <option value="GB">GB</option>
                <option value="TB">TB</option>
              </select>
            </div>
            <span v-if="showUnlimitedReceiveQuotaWarning" class="field-warning">
              Unlimited receive links are not recommended for public use.
            </span>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="receive-parallel-upload-limit">Parallel uploads</label>
              <HelpTooltip text="Controls how many files a public receive page can upload at the same time. Higher values can finish batches faster but use more bandwidth and disk activity." />
            </div>
            <input
              id="receive-parallel-upload-limit"
              v-model.number="settingsDraft.receiveParallelUploadLimit"
              type="number"
              min="0"
              placeholder="Unlimited"
            />
            <span class="field-help">Default: 4 files at a time. Use 0 for no limit.</span>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="receive-upload-mode">Upload mode</label>
              <HelpTooltip text="Auto probes the available upload transports and keeps one server upload session per file. Multipart chunks sends each piece as multipart form data. Binary chunks sends raw binary requests. WebSocket uploads send chunks through a dedicated upload socket. Compressed stream is an experimental single-request gzip upload path." />
            </div>
            <select id="receive-upload-mode" v-model="settingsDraft.receiveUploadMode">
              <option value="Auto">Auto</option>
              <option value="MultipartChunks">Multipart chunks</option>
              <option value="BinaryChunks">Binary chunks</option>
              <option value="WebSocket">WebSocket chunks</option>
              <option value="CompressedStream">Compressed stream (experimental)</option>
            </select>
          </div>

          <div v-if="settingsDraft.receiveUploadMode === 'Auto'" class="field">
            <div class="field-label-row">
              <label for="receive-upload-auto-probe-chunks">Auto probing threshold</label>
              <HelpTooltip text="How many chunks Auto mode uses to compare transports before it prefers the most reliable observed method." />
            </div>
            <input
              id="receive-upload-auto-probe-chunks"
              v-model.number="settingsDraft.receiveUploadAutoProbeChunkCount"
              type="number"
              min="1"
            />
            <span class="field-help">Default: 4 chunks. Minimum: 1 chunk.</span>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="receive-upload-chunk-sizing-mode">Packet sizing</label>
              <HelpTooltip text="Fixed uses the configured request size. Auto lets the host recommend the next packet size from the receive speed and target request time." />
            </div>
            <select id="receive-upload-chunk-sizing-mode" v-model="settingsDraft.receiveUploadChunkSizingMode">
              <option value="Fixed">Fixed size</option>
              <option value="Auto">Auto</option>
            </select>
          </div>

          <div v-if="settingsDraft.receiveUploadChunkSizingMode !== 'Auto'" class="field">
            <div class="field-label-row">
              <label for="receive-upload-chunk-size">Packet size</label>
              <HelpTooltip text="Controls the request size used for large receive-page uploads. Smaller packets retry less data after a failure; larger packets reduce per-request overhead." />
            </div>
            <div class="input-group">
              <input
                id="receive-upload-chunk-size"
                v-model.number="receiveUploadChunkSizeMb"
                type="number"
                min="1"
              />
              <span class="unit-suffix">MB</span>
            </div>
            <span class="field-help">Default: 16 MB. Minimum: 1 MB.</span>
          </div>

          <div v-else class="field">
            <div class="field-label-row">
              <label for="receive-upload-chunk-target-seconds">Target request time</label>
              <HelpTooltip text="The host uses receive speed to recommend packet sizes that should finish near this duration." />
            </div>
            <div class="input-group">
              <input
                id="receive-upload-chunk-target-seconds"
                v-model.number="settingsDraft.receiveUploadChunkTargetSeconds"
                type="number"
                min="5"
              />
              <span class="unit-suffix">seconds</span>
            </div>
            <span class="field-help">Default: 30 seconds. Minimum: 5 seconds.</span>
            <span v-if="showCloudflareTargetTimeoutWarning" class="field-warning">
              Cloudflare can time out proxied requests after 120 seconds. Use a lower target when publishing through Cloudflare.
            </span>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="receive-upload-max-body-size">Max request body size</label>
              <HelpTooltip text="Caps receive-page upload request bodies. Cloudflare Free and Pro allow up to 100 MB per request, so the default leaves a small safety margin." />
            </div>
            <div class="input-group">
              <input
                id="receive-upload-max-body-size"
                v-model.number="receiveUploadMaxBodySizeMb"
                type="number"
                min="1"
              />
              <span class="unit-suffix">MB</span>
            </div>
            <span class="field-help">Default: 95 MB. Cloudflare Free/Pro limit: 100 MB.</span>
            <span v-if="showCloudflareFreePlanBodySizeWarning" class="field-warning">
              The logged-in Cloudflare zone is on the Free plan. Requests over 100 MB can fail with 413.
            </span>
          </div>

          <ToggleField
            v-model="settingsDraft.receiveNotificationsEnabled"
            input-id="receive-notifications-enabled"
            label="Receive notifications"
            help-text="Shows a local notification when uploads finish on this machine."
          />

          <ToggleField
            v-model="settingsDraft.browserManagedDownloadsEnabled"
            input-id="browser-managed-downloads-enabled"
            label="Browser-managed downloads"
            help-text="Uses a browser page for non-inline file downloads so chunks can be verified, retried, paused, and resumed before falling back to direct download."
          />

          <div class="field">
            <div class="field-label-row">
              <label for="browser-managed-download-memory">Managed download memory limit</label>
              <HelpTooltip text="Files at or below this size may be assembled in browser memory. Larger files require streaming save support or direct download fallback." />
            </div>
            <div class="input-group">
              <input
                id="browser-managed-download-memory"
                v-model.number="browserManagedDownloadMaxMemoryMb"
                type="number"
                min="1"
              />
              <span class="unit-suffix">MB</span>
            </div>
            <span class="field-help">Default: 512 MB.</span>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="browser-managed-download-parallel-chunks">Managed download parallel chunks</label>
              <HelpTooltip text="Maximum number of file chunks the browser-managed downloader can fetch at once. It still starts at one and backs off after errors." />
            </div>
            <input
              id="browser-managed-download-parallel-chunks"
              v-model.number="settingsDraft.browserManagedDownloadMaxParallelChunks"
              type="number"
              min="1"
            />
            <span class="field-help">Default: 4 chunks.</span>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="browser-managed-compression-mode">Managed compression</label>
              <HelpTooltip text="Auto allows browser-managed transfers to compare raw and gzip transfer paths for files that may compress well." />
            </div>
            <select id="browser-managed-compression-mode" v-model="settingsDraft.browserManagedCompressionMode">
              <option value="Auto">Auto</option>
              <option value="Off">Off</option>
            </select>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="browser-transfer-encryption-policy">Browser transfer encryption</label>
              <HelpTooltip text="Controls when browser-managed transfers should use application-level encryption. Raw direct downloads cannot be encrypted by this setting." />
            </div>
            <select id="browser-transfer-encryption-policy" v-model="settingsDraft.browserTransferEncryptionPolicy">
              <option value="HttpOnly">HTTP only</option>
              <option value="Always">Always</option>
              <option value="Off">Off</option>
            </select>
          </div>

          <ToggleField
            v-model="settingsDraft.browserTransferDiagnosticsEnabled"
            input-id="browser-transfer-diagnostics-enabled"
            label="Transfer diagnostics"
            help-text="Shows browser-managed transfer decisions, chunk sizing, compression status, retry counts, and fallback reasons on public transfer pages."
          />

          <ToggleField
            v-model="settingsDraft.addFolderReceiveContextMenuButton"
            input-id="folder-receive-context-button"
            label="Folder context menu: Receive files here"
            help-text="Adds a folder context-menu action that creates a public receive link bound to that folder."
          />
        </SettingCard>

        <SettingCard class="settings-card" eyebrow="Cloudflare">
          <div class="managed-block">
            <strong v-if="cloudflaredStatus?.installedVersion">Version: {{ cloudflaredStatus.installedVersion }}</strong>
            <strong v-else>cloudflared not installed</strong>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="cloudflared-path">Cloudflared path override</label>
              <HelpTooltip text="Lets you point the server at a specific cloudflared executable instead of relying on PATH detection." />
            </div>
            <button
              id="cloudflared-path"
              class="file-picker-field"
              type="button"
              @click="emit('pickCloudflaredPath')"
            >
              <span class="file-picker-value" :class="{ empty: !settingsDraft.cloudflaredPathOverride }">
                {{ settingsDraft.cloudflaredPathOverride || 'Using auto-detected cloudflared path' }}
              </span>
            </button>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="managed-domain">Domain</label>
              <HelpTooltip text="Choose which Cloudflare-managed domain should host your public links." />
            </div>
            <select id="managed-domain" v-model="selectedDomain" :disabled="!availableDomains.length">
              <option value="" disabled>Select a domain</option>
              <option v-for="domain in availableDomains" :key="domain.zoneId" :value="domain.name">
                {{ domain.name }}
              </option>
            </select>
          </div>

          <div class="field">
            <div class="field-label-row">
              <label for="managed-subdomain">Subdomain</label>
              <HelpTooltip text="Subdomain prefix to use under the selected domain for managed Cloudflare sharing." />
            </div>
            <input id="managed-subdomain" v-model="managedSubdomain" placeholder="share" />
          </div>

          <div v-if="managedAvailability" class="managed-block">
            <strong>{{ managedAvailability.hostname }}</strong>
            <span>{{ managedAvailability.message }}</span>
          </div>

          <div class="card-actions column">
            <button
              v-if="!cloudflaredStatus?.installed"
              class="secondary"
              type="button"
              @click="emit('installCloudflared')"
            >
              Install with winget
            </button>
            <button
              v-if="cloudflaredStatus?.updateAvailable"
              class="secondary"
              type="button"
              @click="emit('updateCloudflared')"
            >
              Update cloudflared
            </button>
            <button class="primary" type="button" @click="emit('toggleCloudflareLogin')">
              {{ cloudflareLoginLabel }}
            </button>
            <button class="primary" type="button" @click="emit('createManagedTunnel')">Save</button>
          </div>
        </SettingCard>

        <SettingCard class="settings-card" eyebrow="Debug">
          <ToggleField
            v-model="settingsDraft.showLogs"
            input-id="show-logs"
            label="Show logs"
            help-text="Shows the log views in the sidebar so you can inspect server and cloudflared output."
          />
        </SettingCard>
      </div>
    </section>
  </section>
</template>
