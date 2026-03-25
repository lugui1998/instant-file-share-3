<script setup lang="ts">
import { computed } from 'vue'
import type { AgentShareRecord } from '../../agentBridge'
import PanelHeader from '../PanelHeader.vue'

const props = defineProps<{
  shares: AgentShareRecord[]
}>()

const emit = defineEmits<{
  copyShare: [share: AgentShareRecord]
  revokeShare: [shareId: string]
}>()

const activeShares = computed(() => props.shares.filter((share) => share.state === 'Active'))

function formatTimestamp(value: string) {
  return new Date(value).toLocaleString()
}
</script>

<template>
  <section class="panel shares-panel">
    <PanelHeader eyebrow="Shares" title="" />

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
