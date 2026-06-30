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
  showInExplorer: [shareId: string]
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

function getShareTypeLabel(share: AgentShareRecord) {
  if (share.itemKind === 'Receive') {
    return 'Receive link'
  }

  if (share.shareKind !== 'Folder' && share.itemKind !== 'Folder') {
    return 'File share'
  }

  if (share.canBrowseFolderContents && share.canDownloadFolderAsZip) {
    return share.primaryFolderEntryPoint === 'Zip' ? 'Folder · ZIP primary' : 'Folder · Browse primary'
  }

  return share.canDownloadFolderAsZip ? 'Folder · ZIP only' : 'Folder · Browse only'
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
              <div class="status-copy">
                <span>{{ getShareTypeLabel(share) }}</span>
              </div>
            </td>
            <td>
              <template v-if="share.itemKind === 'Receive'">--</template>
              <template v-else>{{ share.useCount }}<span v-if="share.maxUses"> / {{ share.maxUses }}</span></template>
            </td>
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
              <button
                aria-label="Show in Explorer"
                class="secondary compact-icon-button"
                title="Show in Explorer"
                type="button"
                @click="emit('showInExplorer', share.id)"
              >
                <svg aria-hidden="true" class="folder-icon" viewBox="0 0 16 16">
                  <path
                    d="M1.75 3A1.75 1.75 0 0 1 3.5 1.25h2.18c.4 0 .78.14 1.08.4l1.02.85h4.72A1.75 1.75 0 0 1 14.25 4v1.02a1.7 1.7 0 0 1-.34 1.02l-1.9 2.54A1.75 1.75 0 0 1 10.62 9H3.5A1.75 1.75 0 0 1 1.75 7.25V3Zm1.5.1v4.15c0 .14.11.25.25.25h7.12c.08 0 .15-.04.2-.1l1.9-2.54V4a.25.25 0 0 0-.25-.25H7.5l-1.43-1.2a.24.24 0 0 0-.16-.05H3.5a.25.25 0 0 0-.25.25Zm1.6 7.15a.75.75 0 0 1 .75-.75h5.9a.75.75 0 0 1 0 1.5h-5.9a.75.75 0 0 1-.75-.75Z"
                    fill="currentColor"
                  />
                </svg>
              </button>
              <button class="danger compact-button revoke-button" type="button" @click="emit('revokeShare', share.id)">
                <svg aria-hidden="true" class="trash-icon" viewBox="0 0 16 16">
                  <path
                    d="M6 2.25h4a1 1 0 0 1 1 1V4h2a.75.75 0 0 1 0 1.5h-.54l-.63 7.31A1.75 1.75 0 0 1 10.08 14H5.92a1.75 1.75 0 0 1-1.74-1.19L3.54 5.5H3A.75.75 0 0 1 3 4h2v-.75a1 1 0 0 1 1-1Zm3.5 1.75V3.75h-3V4h3Zm-3.18 7.25a.75.75 0 1 0 1.5 0V7.25a.75.75 0 0 0-1.5 0v4Zm3.36 0a.75.75 0 1 0 1.5 0V7.25a.75.75 0 0 0-1.5 0v4Z"
                    fill="currentColor"
                  />
                </svg>
                <span>Revoke</span>
              </button>
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
