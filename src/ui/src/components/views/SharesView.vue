<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { AgentShareRecord } from '../../agentBridge'
import PanelHeader from '../PanelHeader.vue'
import TablePagination from '../TablePagination.vue'

const props = defineProps<{
  shares: AgentShareRecord[]
  itemsPerPage: number
}>()

const emit = defineEmits<{
  copyShare: [share: AgentShareRecord]
  revokeShare: [shareId: string]
}>()

const activeShares = computed(() => props.shares.filter((share) => share.state === 'Active'))
const currentPage = ref(1)
const pageCount = computed(() => {
  if (props.itemsPerPage <= 0) {
    return activeShares.value.length > 0 ? 1 : 0
  }

  return Math.max(1, Math.ceil(activeShares.value.length / props.itemsPerPage))
})
const pagedShares = computed(() => {
  if (props.itemsPerPage <= 0) {
    return activeShares.value
  }

  const startIndex = (currentPage.value - 1) * props.itemsPerPage
  return activeShares.value.slice(startIndex, startIndex + props.itemsPerPage)
})

function formatTimestamp(value: string) {
  return new Date(value).toLocaleString()
}

watch([activeShares, () => props.itemsPerPage], () => {
  currentPage.value = Math.min(currentPage.value, Math.max(1, pageCount.value || 1))
}, { deep: true })
</script>

<template>
  <section class="panel shares-panel">
    <PanelHeader eyebrow="Shares" title="">
      <TablePagination
        :current-page="currentPage"
        :page-count="pageCount"
        :total-items="activeShares.length"
        @update-page="currentPage = $event"
      />
    </PanelHeader>

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
          <tr v-for="share in pagedShares" :key="share.id">
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
          <tr v-if="!pagedShares.length">
            <td class="empty-state" colspan="4">No active shares yet.</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
