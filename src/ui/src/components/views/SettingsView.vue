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
            help-text="Launches the server automatically when you sign in to Windows."
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

          <ToggleField
            v-model="settingsDraft.receiveNotificationsEnabled"
            input-id="receive-notifications-enabled"
            label="Receive notifications"
            help-text="Shows a local notification when uploads finish on this machine."
          />
          <ToggleField
            v-model="settingsDraft.addFolderReceiveContextMenuButton"
            input-id="folder-receive-context-button"
            label="Folder context menu: Receive files here"
            help-text="Adds a folder context-menu action that creates a public receive link bound to that folder."
          />
        </SettingCard>
      </div>
    </section>
  </section>
</template>
