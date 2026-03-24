<script setup lang="ts">
import { computed } from 'vue'
import type { AgentShareRecord, PublishMode } from '../../agentBridge'
import PanelHeader from '../PanelHeader.vue'

const props = defineProps<{
  pending: boolean
  shares: AgentShareRecord[]
}>()

const draftFilePath = defineModel<string>('draftFilePath', { required: true })
const draftMode = defineModel<PublishMode>('draftMode', { required: true })

const emit = defineEmits<{
  copyShare: [share: AgentShareRecord]
  createShare: []
  revokeShare: [shareId: string]
}>()

const activeShares = computed(() => props.shares.filter((share) => share.state === 'Active'))
const createDisabled = computed(() => props.pending || !draftFilePath.value.trim())
const activeSharesTitle = computed(
  () => `${activeShares.value.length} active link${activeShares.value.length === 1 ? '' : 's'}`,
)

function formatTimestamp(value: string) {
  return new Date(value).toLocaleString()
}
</script>

<template>
  <section class="composer">
    <div class="field grow">
      <label for="file-path">Share file</label>
      <input id="file-path" v-model="draftFilePath" placeholder="C:\Users\me\Desktop\large-file.zip" />
    </div>

    <div class="field compact">
      <label for="mode">Mode</label>
      <select id="mode" v-model="draftMode">
        <option value="QuickTunnel">Quick Tunnel</option>
        <option value="ManagedCloudflare">Custom Cloudflare Domain</option>
        <option value="Manual">Manual</option>
      </select>
    </div>

    <button class="primary launch" :disabled="createDisabled" type="button" @click="emit('createShare')">
      Create share
    </button>
  </section>

  <section class="panel shares-panel">
    <PanelHeader eyebrow="Shares" :title="activeSharesTitle" />

    <div class="table-shell shares-shell">
      <table class="shares-table">
        <thead>
          <tr>
            <th>File</th>
            <th>Uses</th>
            <th>Created</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="share in activeShares" :key="share.id">
            <td>
              <strong>{{ share.fileName }}</strong>
            </td>
            <td>{{ share.useCount }}<span v-if="share.maxUses"> / {{ share.maxUses }}</span></td>
            <td>{{ formatTimestamp(share.createdAtUtc) }}</td>
            <td class="actions-cell">
              <button
                aria-label="Copy link"
                class="secondary compact-icon-button"
                title="Copy link"
                type="button"
                @click="emit('copyShare', share)"
              >
                <span aria-hidden="true">⧉</span>
              </button>
              <button class="danger compact-button" type="button" @click="emit('revokeShare', share.id)">Revoke</button>
            </td>
          </tr>
          <tr v-if="!activeShares.length">
            <td class="empty-state" colspan="4">No active shares yet.</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
