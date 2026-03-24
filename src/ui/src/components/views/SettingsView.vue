<script setup lang="ts">
import { computed } from 'vue'
import type {
  AppSettings,
  CloudflaredDashboardStatus,
  CloudflareManagedAvailability,
  CloudflareManagedStatus,
} from '../../agentBridge'
import type { BandwidthUnit } from '../../types/ui'
import SettingCard from '../settings/SettingCard.vue'
import ToggleField from '../settings/ToggleField.vue'

const props = defineProps<{
  cloudflaredStatus: CloudflaredDashboardStatus | null
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
</script>

<template>
  <section class="settings-grid">
    <SettingCard eyebrow="Sharing">
      <div class="field">
        <label for="default-publish-mode">Default publish mode</label>
        <select id="default-publish-mode" v-model="settingsDraft.defaultPublishMode">
          <option value="QuickTunnel">Quick Tunnel</option>
          <option value="ManagedCloudflare">Custom Cloudflare Domain</option>
          <option value="Manual">Manual</option>
        </select>
      </div>

      <div class="field">
        <label for="default-expiry-hours">Default expiry hours</label>
        <input id="default-expiry-hours" v-model.number="settingsDraft.defaultExpiryHours" type="number" min="1" />
      </div>

      <div class="field">
        <label for="default-max-uses">Default max uses</label>
        <input
          id="default-max-uses"
          v-model.number="settingsDraft.defaultMaxUses"
          type="number"
          min="0"
          placeholder="Unlimited"
        />
      </div>

      <div class="field">
        <label for="file-change-behavior">Changed file behavior</label>
        <select id="file-change-behavior" v-model="settingsDraft.fileChangeBehavior">
          <option value="Strict">Strict</option>
          <option value="Lenient">Lenient</option>
        </select>
      </div>

      <ToggleField v-model="settingsDraft.friendlyUrlsEnabled" input-id="friendly-urls" label="Friendly URLs enabled" />
      <ToggleField
        v-model="settingsDraft.keepAwakeWhileTransferring"
        input-id="keep-awake"
        label="Keep the PC awake while transfers are active"
      />
    </SettingCard>

    <SettingCard eyebrow="Server">
      <ToggleField v-model="settingsDraft.startOnLogin" input-id="start-on-login" label="Start on login" />

      <div class="field">
        <label for="bandwidth-limit">Bandwidth limit bytes/sec</label>
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
        <label for="manual-bind-address">Manual mode bind address</label>
        <input id="manual-bind-address" v-model="settingsDraft.manualBindAddress" placeholder="127.0.0.1 or 0.0.0.0" />
      </div>

      <div class="field">
        <label for="manual-public-port">Manual mode public port</label>
        <input id="manual-public-port" v-model.number="settingsDraft.manualPublicPort" type="number" min="1" max="65535" />
      </div>

      <div class="field">
        <label for="manual-base-url">Manual base URL override</label>
        <input id="manual-base-url" v-model="settingsDraft.manualBaseUrl" placeholder="Optional https://files.example.com" />
      </div>

      <div class="field">
        <label for="local-api-port">Local API port</label>
        <input id="local-api-port" v-model.number="settingsDraft.localApiPort" type="number" min="1" max="65535" />
      </div>

      <p v-if="saveMessage" class="save-message">{{ saveMessage }}</p>
    </SettingCard>

    <SettingCard eyebrow="Cloudflare">
      <div class="managed-block">
        <strong v-if="cloudflaredStatus?.installedVersion">Version: {{ cloudflaredStatus.installedVersion }}</strong>
        <strong v-else>cloudflared not installed</strong>
      </div>

      <div class="field">
        <label for="cloudflared-path">Cloudflared path override</label>
        <input id="cloudflared-path" accept=".exe" type="file" @click.prevent="emit('pickCloudflaredPath')" />
        <span v-if="settingsDraft.cloudflaredPathOverride" class="path-hint">{{ settingsDraft.cloudflaredPathOverride }}</span>
      </div>

      <div class="field">
        <label for="managed-domain">Domain</label>
        <select id="managed-domain" v-model="selectedDomain" :disabled="!availableDomains.length">
          <option value="" disabled>Select a domain</option>
          <option v-for="domain in availableDomains" :key="domain.zoneId" :value="domain.name">
            {{ domain.name }}
          </option>
        </select>
      </div>

      <div class="field">
        <label for="managed-subdomain">Subdomain</label>
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

    <SettingCard eyebrow="Debug">
      <ToggleField v-model="settingsDraft.showLogs" input-id="show-logs" label="Show logs" />
      <ToggleField
        v-model="settingsDraft.addFileContextMenuButton"
        input-id="file-context-button"
        label="Add button to file context menu"
      />
    </SettingCard>
  </section>
</template>
