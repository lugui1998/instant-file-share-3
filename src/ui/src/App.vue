<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { agentBridge, type CloudflareManagedAvailability, type CloudflareManagedStatus } from './agentBridge'

type ViewKey = 'shares' | 'transfers' | 'settings' | 'diagnostics'
type ShareItem = {
  id: string
  token: string
  fileName: string
  filePath: string
  publicBaseUrl: string
  createdAtUtc: string
  maxUses?: number | null
  useCount: number
  state: string
  publishMode: string
  brokenReason?: string | null
}

type RuntimeData = {
  shares: ShareItem[]
  transfers: Array<Record<string, any>>
  settings: Record<string, any>
  cloudflared: Record<string, any>
}

const activeView = ref<ViewKey>('shares')
const runtime = ref<RuntimeData | null>(null)
const pending = ref(false)
const error = ref<string | null>(null)
const draftFilePath = ref('')
const draftMode = ref('QuickTunnel')
const managedStatus = ref<CloudflareManagedStatus | null>(null)
const managedAvailability = ref<CloudflareManagedAvailability | null>(null)
const selectedDomain = ref('')
const managedSubdomain = ref('share')
let managedAvailabilityTimer: ReturnType<typeof setTimeout> | null = null

const shares = computed(() => runtime.value?.shares ?? [])
const transfers = computed(() => runtime.value?.transfers ?? [])
const settings = computed(() => runtime.value?.settings ?? {})
const cloudflared = computed(() => runtime.value?.cloudflared ?? {})

async function loadRuntime() {
  pending.value = true
  error.value = null

  try {
    runtime.value = await agentBridge.getRuntime()
    if (typeof runtime.value.settings?.defaultPublishMode === 'string') {
      draftMode.value = runtime.value.settings.defaultPublishMode
    }
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Failed to contact the local agent.'
  } finally {
    pending.value = false
  }

  try {
    managedStatus.value = await agentBridge.getManagedCloudflareStatus()
    const domains = managedStatus.value.domains ?? []
    if (!selectedDomain.value && domains.length > 0) {
      selectedDomain.value = domains[0].name
    }
    await refreshManagedAvailability()
  } catch {
    managedStatus.value = null
    managedAvailability.value = null
  }
}

async function createShare() {
  if (!draftFilePath.value.trim()) {
    error.value = 'Enter a local file path to create a test share from the dashboard.'
    return
  }

  pending.value = true
  error.value = null

  try {
    await agentBridge.createShare(draftFilePath.value.trim(), draftMode.value)
    draftFilePath.value = ''
    await loadRuntime()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Share creation failed.'
  } finally {
    pending.value = false
  }
}

async function revokeShare(shareId: string) {
  try {
    await agentBridge.revokeShare(shareId)
    await loadRuntime()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Failed to revoke the share.'
  }
}

async function detectCloudflared() {
  try {
    await agentBridge.detectCloudflared()
    await loadRuntime()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Failed to detect cloudflared.'
  }
}

async function installCloudflared() {
  try {
    await agentBridge.installCloudflared()
    await loadRuntime()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Failed to install cloudflared.'
  }
}

async function updateCloudflared() {
  try {
    await agentBridge.updateCloudflared()
    await loadRuntime()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Failed to update cloudflared.'
  }
}

async function startCloudflareLogin() {
  try {
    await agentBridge.startCloudflareLogin()
    await loadRuntime()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Failed to start the Cloudflare login flow.'
  }
}

async function createManagedTunnel() {
  if (!selectedDomain.value.trim()) {
    error.value = 'Select a domain first.'
    return
  }

  if (!managedSubdomain.value.trim()) {
    error.value = 'Enter the subdomain you want to use for file sharing.'
    return
  }

  try {
    await agentBridge.createManagedTunnel(selectedDomain.value.trim(), managedSubdomain.value.trim())
    await loadRuntime()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Failed to create the managed tunnel.'
  }
}

async function refreshManagedAvailability() {
  if (!managedStatus.value?.loggedIn || !selectedDomain.value.trim()) {
    managedAvailability.value = null
    return
  }

  managedAvailability.value = await agentBridge.checkManagedTunnelAvailability(
    selectedDomain.value.trim(),
    managedSubdomain.value.trim(),
  )
}

let disconnect: () => void = () => {}

onMounted(async () => {
  disconnect = agentBridge.connectRuntime(async () => {
    await loadRuntime()
  })
  await loadRuntime()
})

onUnmounted(() => {
  if (managedAvailabilityTimer) {
    clearTimeout(managedAvailabilityTimer)
  }
  disconnect()
})

watch([selectedDomain, managedSubdomain], () => {
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
</script>

<template>
  <div class="shell">
    <aside class="rail">
      <div class="brand">
        <div class="brand-mark">IF</div>
        <div>
          <p class="eyebrow">Instant File Share</p>
          <h1>Control Deck</h1>
        </div>
      </div>

      <nav class="nav">
        <button :class="{ active: activeView === 'shares' }" @click="activeView = 'shares'">Shares</button>
        <button :class="{ active: activeView === 'transfers' }" @click="activeView = 'transfers'">Transfers</button>
        <button :class="{ active: activeView === 'settings' }" @click="activeView = 'settings'">Settings</button>
        <button :class="{ active: activeView === 'diagnostics' }" @click="activeView = 'diagnostics'">Diagnostics</button>
      </nav>

      <div class="status-card">
        <p class="eyebrow">Agent</p>
        <strong>{{ pending ? 'Refreshing' : 'Connected' }}</strong>
        <span>Local API `127.0.0.1:46430`</span>
      </div>
    </aside>

    <main class="content">
      <section class="composer">
        <div class="field grow">
          <label for="file-path">Quick test share</label>
          <input id="file-path" v-model="draftFilePath" placeholder="C:\Users\me\Desktop\large-file.zip" />
        </div>

        <div class="field compact">
          <label for="mode">Mode</label>
          <select id="mode" v-model="draftMode">
            <option>QuickTunnel</option>
            <option>ManagedCloudflare</option>
            <option>Manual</option>
          </select>
        </div>

        <button class="secondary launch" @click="loadRuntime">Refresh</button>
        <button class="primary launch" @click="createShare">Create share</button>
      </section>

      <p v-if="error" class="error-banner">{{ error }}</p>

      <section v-if="activeView === 'shares'" class="panel">
        <div class="panel-header">
          <div>
            <p class="eyebrow">Shares</p>
            <h3>{{ shares.length }} tracked link<span v-if="shares.length !== 1">s</span></h3>
          </div>
          <span class="pill">Friendly URLs by default</span>
        </div>

        <div class="grid">
          <article v-for="share in shares" :key="share.id" class="share-card">
            <div class="share-head">
              <div>
                <h4>{{ share.fileName }}</h4>
                <p>{{ share.publicBaseUrl }}/s/{{ share.token }}</p>
              </div>
              <span class="state" :data-state="share.state">{{ share.state }}</span>
            </div>

            <dl class="meta">
              <div>
                <dt>Mode</dt>
                <dd>{{ share.publishMode }}</dd>
              </div>
              <div>
                <dt>Uses</dt>
                <dd>{{ share.useCount }}<span v-if="share.maxUses"> / {{ share.maxUses }}</span></dd>
              </div>
              <div>
                <dt>Created</dt>
                <dd>{{ new Date(share.createdAtUtc).toLocaleString() }}</dd>
              </div>
              <div>
                <dt>Path</dt>
                <dd class="path">{{ share.filePath }}</dd>
              </div>
            </dl>

            <p v-if="share.brokenReason" class="warning">{{ share.brokenReason }}</p>

            <div class="card-actions">
              <button class="secondary" @click="revokeShare(share.id)">Revoke</button>
            </div>
          </article>
        </div>
      </section>

      <section v-else-if="activeView === 'transfers'" class="panel">
        <div class="panel-header">
          <div>
            <p class="eyebrow">Transfers</p>
            <h3>{{ transfers.length }} recent event<span v-if="transfers.length !== 1">s</span></h3>
          </div>
        </div>

        <div class="table-shell">
          <table>
            <thead>
              <tr>
                <th>File</th>
                <th>Remote</th>
                <th>Bytes</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(transfer, index) in transfers" :key="index">
                <td>{{ transfer.fileName ?? 'Unknown file' }}</td>
                <td>{{ transfer.remoteAddress ?? 'n/a' }}</td>
                <td>{{ transfer.bytesSent ?? 0 }}</td>
                <td>{{ transfer.succeeded ? 'Completed' : 'Failed / partial' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section v-else-if="activeView === 'settings'" class="panel settings-grid">
        <article class="setting-card">
          <p class="eyebrow">Sharing defaults</p>
          <h3>Current agent settings</h3>
          <pre>{{ JSON.stringify(settings, null, 2) }}</pre>
        </article>

        <article class="setting-card">
          <p class="eyebrow">Cloudflared</p>
          <h3>Tunnel runtime</h3>
          <pre>{{ JSON.stringify(cloudflared, null, 2) }}</pre>
        </article>
      </section>

      <section v-else class="panel diagnostics-grid">
        <article class="diagnostic-card">
          <p class="eyebrow">Quick actions</p>
          <div class="card-actions column">
            <button class="secondary" @click="detectCloudflared">Detect cloudflared</button>
            <button class="secondary" @click="installCloudflared">Install with winget</button>
            <button class="secondary" @click="updateCloudflared">Update managed binary</button>
            <button class="primary" @click="startCloudflareLogin">Start Cloudflare login</button>
          </div>
        </article>

        <article class="diagnostic-card">
          <p class="eyebrow">Managed Cloudflare</p>
          <div class="managed-block">
            <strong>{{ managedStatus?.loggedIn ? 'Logged in' : 'Not logged in' }}</strong>
            <span>{{ managedStatus?.message || 'No managed Cloudflare status available yet.' }}</span>
            <span v-if="managedStatus?.configuredHostname">Current hostname: {{ managedStatus.configuredHostname }}</span>
            <span v-if="managedStatus?.configuredTunnelName">Tunnel: {{ managedStatus.configuredTunnelName }}</span>
          </div>

          <div class="field">
            <label for="managed-domain">Domain</label>
            <select id="managed-domain" v-model="selectedDomain" :disabled="!(managedStatus?.domains?.length)">
              <option value="" disabled>Select a domain</option>
              <option v-for="domain in managedStatus?.domains ?? []" :key="domain.zoneId" :value="domain.name">
                {{ domain.name }}
              </option>
            </select>
          </div>

          <div class="field">
            <label for="managed-subdomain">Subdomain</label>
            <input id="managed-subdomain" v-model="managedSubdomain" placeholder="share" />
          </div>

          <div v-if="managedAvailability" class="managed-block">
            <strong>Managed target status</strong>
            <span>Hostname: {{ managedAvailability.hostname }}</span>
            <span>Tunnel name: {{ managedAvailability.tunnelName }}</span>
            <span>Tunnel exists: {{ managedAvailability.tunnelExists ? 'yes' : 'no' }}</span>
            <span>Subdomain exists: {{ managedAvailability.hostnameExists ? 'yes' : 'no' }}</span>
            <span>{{ managedAvailability.message }}</span>
          </div>

          <div class="card-actions column">
            <button class="primary" @click="createManagedTunnel">Create managed tunnel</button>
          </div>
        </article>

        <article class="diagnostic-card">
          <p class="eyebrow">Support snapshot</p>
          <pre>{{ JSON.stringify(runtime, null, 2) }}</pre>
        </article>
      </section>
    </main>
  </div>
</template>
