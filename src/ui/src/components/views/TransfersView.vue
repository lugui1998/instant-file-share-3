<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import type { TransferRecord } from '../../agentBridge'
import HelpTooltip from '../settings/HelpTooltip.vue'
import PanelHeader from '../PanelHeader.vue'
import TablePagination from '../TablePagination.vue'

const props = defineProps<{
  transfers: TransferRecord[]
  itemsPerPage: number
  removingTransferId: string | null
  clearingHistory: boolean
}>()

const emit = defineEmits<{
  removeTransfer: [transferId: string]
}>()

const pausedThresholdMs = 1500
const now = ref(Date.now())
const currentPage = ref(1)
const filteredTransfers = computed(() =>
  props.transfers.map((transfer) => ({
    ...transfer,
    isPaused:
      transfer.state === 'Paused' ||
      (transfer.isActive && now.value - new Date(transfer.lastUpdatedAtUtc).getTime() >= pausedThresholdMs),
  })),
)
const pageCount = computed(() => {
  if (props.itemsPerPage <= 0) {
    return filteredTransfers.value.length > 0 ? 1 : 0
  }

  return Math.max(1, Math.ceil(filteredTransfers.value.length / props.itemsPerPage))
})
const displayedTransfers = computed(() => {
  if (props.itemsPerPage <= 0) {
    return filteredTransfers.value
  }

  const startIndex = (currentPage.value - 1) * props.itemsPerPage
  return filteredTransfers.value.slice(startIndex, startIndex + props.itemsPerPage)
})

let refreshTimer: ReturnType<typeof setInterval> | null = null

function isZipStreamTransfer(transfer: TransferRecord) {
  return transfer.transferKind === 'FolderZipDownload'
}

function isUploadTransfer(transfer: TransferRecord) {
  return transfer.transferKind === 'FileUpload'
}

function hasEstimatedProgress(transfer: TransferRecord) {
  return (transfer.progressTotalBytes ?? 0) > 0
}

function isCompletedTransfer(transfer: TransferRecord) {
  return transfer.state === 'Completed' || transfer.succeeded
}

function hasKnownTransferTotal(transfer: TransferRecord) {
  return isZipStreamTransfer(transfer)
    ? hasEstimatedProgress(transfer)
    : transfer.totalBytes > 0
}

function shouldShowProgressBar(transfer: TransferRecord) {
  return !isCompletedTransfer(transfer) && hasKnownTransferTotal(transfer)
}

function clampProgress(value: number) {
  return Math.max(0, Math.min(100, value))
}

function getProgressPercent(transfer: TransferRecord) {
  const totalBytes = hasEstimatedProgress(transfer)
    ? transfer.progressTotalBytes ?? 0
    : transfer.totalBytes
  const progressBytes = hasEstimatedProgress(transfer)
    ? transfer.progressBytes ?? 0
    : transfer.bytesSent

  if (totalBytes <= 0) {
    return transfer.isActive ? 0 : 100
  }

  return clampProgress((progressBytes / totalBytes) * 100)
}

function formatBytes(value: number) {
  const normalized = Math.max(0, value)

  if (normalized < 1024) {
    return `${Math.round(normalized)} B`
  }

  if (normalized < 1024 * 1024) {
    return `${formatCompactNumber(normalized / 1024)} KB`
  }

  if (normalized < 1024 * 1024 * 1024) {
    return `${formatCompactNumber(normalized / (1024 * 1024))} MB`
  }

  return `${formatCompactNumber(normalized / (1024 * 1024 * 1024))} GB`
}

function formatCompactNumber(value: number) {
  if (value >= 100) {
    return Math.round(value).toString()
  }

  return value.toFixed(1).replace(/\.0$/, '')
}

function formatTransferSpeed(value: number) {
  const normalized = Math.max(0, value)

  if (normalized >= 1024 * 1024 * 1024) {
    return `${formatCompactNumber(normalized / (1024 * 1024 * 1024))} GB/s`
  }

  if (normalized >= 1024 * 1024) {
    return `${formatCompactNumber(normalized / (1024 * 1024))} MB/s`
  }

  return `${formatCompactNumber(normalized / 1024)} KB/s`
}

function formatProgressLabel(transfer: TransferRecord) {
  const sent = formatBytes(transfer.bytesSent)
  const total = transfer.totalBytes > 0 ? formatBytes(transfer.totalBytes) : '?'
  return `${sent} / ${total}`
}

function formatEstimatedProgressLabel(transfer: TransferRecord) {
  const progressBytes = formatBytes(transfer.progressBytes ?? 0)
  const progressTotalBytes = formatBytes(transfer.progressTotalBytes ?? 0)
  return `${progressBytes} / ${progressTotalBytes} source processed`
}

function getZipProgressLabel(transfer: TransferRecord) {
  const sent = transfer.bytesSent > 0 ? formatBytes(transfer.bytesSent) : 'Preparing stream'
  return formatSpeedLabel(transfer)
    ? `${sent} • ${formatSpeedLabel(transfer)}`
    : sent
}

function getTransferSpeedBytesPerSecond(transfer: TransferRecord) {
  const startedAt = new Date(transfer.startedAtUtc).getTime()
  const finishedAt = transfer.completedAtUtc
    ? new Date(transfer.completedAtUtc).getTime()
    : transfer.state === 'InProgress'
      ? now.value
      : new Date(transfer.lastUpdatedAtUtc).getTime()
  const elapsedMs = finishedAt - startedAt

  if (elapsedMs <= 0 || transfer.bytesSent <= 0) {
    return null
  }

  return transfer.bytesSent / (elapsedMs / 1000)
}

function formatSpeedLabel(transfer: TransferRecord) {
  const speed = getTransferSpeedBytesPerSecond(transfer)
  return speed ? formatTransferSpeed(speed) : null
}

function getStatusLabel(transfer: TransferRecord) {
  if (transfer.requesterName) {
    return 'Crawler preview'
  }

  if ('isPaused' in transfer && transfer.isPaused) {
    return 'Stopped'
  }

  if (transfer.state === 'InProgress' || transfer.isActive) {
    return 'In progress'
  }

  if (transfer.state === 'Completed' || transfer.succeeded) {
    return isUploadTransfer(transfer) ? 'Uploaded' : 'Completed'
  }

  return 'Failed / partial'
}

function getRemotePrimaryLabel(transfer: TransferRecord) {
  if (isUploadTransfer(transfer)) {
    return transfer.remoteAddress ?? 'Uploader'
  }

  return transfer.requesterName ?? transfer.remoteAddress ?? 'n/a'
}

function isCrawlerTransfer(transfer: TransferRecord) {
  return Boolean(transfer.requesterName)
}

function isRemoveDisabled(transfer: TransferRecord) {
  return transfer.isActive || props.clearingHistory || props.removingTransferId === transfer.id
}

function getRemoveActionTitle(transfer: TransferRecord) {
  if (transfer.isActive) {
    return 'In-progress transfers cannot be removed.'
  }

  return 'Remove history entry'
}

onMounted(() => {
  refreshTimer = setInterval(() => {
    now.value = Date.now()
  }, 500)
})

onUnmounted(() => {
  if (refreshTimer) {
    clearInterval(refreshTimer)
  }
})

watch([filteredTransfers, () => props.itemsPerPage], () => {
  currentPage.value = Math.min(currentPage.value, Math.max(1, pageCount.value || 1))
}, { deep: true })
</script>

<template>
  <section class="panel">
    <PanelHeader eyebrow="History" title="">
      <TablePagination
        :current-page="currentPage"
        :page-count="pageCount"
        :total-items="filteredTransfers.length"
        @update-page="currentPage = $event"
      />
    </PanelHeader>

    <div class="table-shell">
      <table class="transfers-table">
        <thead>
          <tr>
            <th>File</th>
            <th>Remote</th>
            <th>Progress</th>
            <th>Status</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="transfer in displayedTransfers" :key="transfer.id">
            <td>{{ transfer.fileName ?? 'Unknown file' }}</td>
            <td>
              <div class="status-copy">
                <strong>{{ getRemotePrimaryLabel(transfer) }}</strong>
                <span v-if="transfer.requesterName && transfer.remoteAddress">{{ transfer.remoteAddress }}</span>
              </div>
            </td>
            <td class="progress-cell">
              <div v-if="isCrawlerTransfer(transfer)" class="status-copy">
                <div class="progress-inline-label">
                  <strong>Fetched Metadata</strong>
                  <HelpTooltip text="Platforms send these requests to read page metadata and build the rich embed preview without downloading the shared file." />
                </div>
                <span>No file download</span>
              </div>
              <template v-else-if="isZipStreamTransfer(transfer) && hasEstimatedProgress(transfer)">
                <div class="progress-meta">
                  <div class="progress-inline-label">
                    <strong>{{ Math.round(getProgressPercent(transfer)) }}% estimated</strong>
                    <HelpTooltip text="Streamed ZIP size is not known ahead of time, so this bar estimates progress from how much source data has been compressed into the archive." />
                  </div>
                  <span>
                    {{ formatEstimatedProgressLabel(transfer) }}
                    <template v-if="formatSpeedLabel(transfer)">
                      • {{ formatBytes(transfer.bytesSent) }} sent
                      • {{ formatSpeedLabel(transfer) }}
                    </template>
                  </span>
                </div>
                <div v-if="shouldShowProgressBar(transfer)" class="progress-track" :aria-label="formatEstimatedProgressLabel(transfer)" role="progressbar" :aria-valuemin="0" :aria-valuemax="100" :aria-valuenow="Math.round(getProgressPercent(transfer))">
                  <div class="progress-fill" :class="{ active: transfer.isActive && !transfer.isPaused, paused: transfer.isPaused }" :style="{ width: `${getProgressPercent(transfer)}%` }" />
                </div>
              </template>
              <div v-else-if="isZipStreamTransfer(transfer)" class="status-copy">
                <div class="progress-inline-label">
                  <strong>Streaming ZIP</strong>
                  <HelpTooltip text="ZIP archives are compressed live while they are sent, so the final total size is not known ahead of time and a progress bar cannot be shown." />
                </div>
                <span>{{ getZipProgressLabel(transfer) }}</span>
              </div>
              <template v-else>
                <div class="progress-meta">
                  <strong>{{ Math.round(getProgressPercent(transfer)) }}%</strong>
                  <span>
                    {{ formatProgressLabel(transfer) }}
                    <template v-if="formatSpeedLabel(transfer)">
                      • {{ formatSpeedLabel(transfer) }}
                    </template>
                  </span>
                </div>
                <div v-if="shouldShowProgressBar(transfer)" class="progress-track" :aria-label="formatProgressLabel(transfer)" role="progressbar" :aria-valuemin="0" :aria-valuemax="100" :aria-valuenow="Math.round(getProgressPercent(transfer))">
                  <div class="progress-fill" :class="{ active: transfer.isActive && !transfer.isPaused, paused: transfer.isPaused }" :style="{ width: `${getProgressPercent(transfer)}%` }" />
                </div>
              </template>
            </td>
            <td>
              <div class="status-copy">
                <strong>{{ getStatusLabel(transfer) }}</strong>
                <span v-if="transfer.error">{{ transfer.error }}</span>
              </div>
            </td>
            <td class="transfer-actions-cell">
              <div class="transfer-actions">
                <button
                  class="danger compact-icon-button"
                  type="button"
                  :disabled="isRemoveDisabled(transfer)"
                  :title="getRemoveActionTitle(transfer)"
                  aria-label="Remove history entry"
                  @click="emit('removeTransfer', transfer.id)"
                >
                  <svg aria-hidden="true" class="trash-icon" viewBox="0 0 16 16">
                    <path
                      d="M6 2.25h4a1 1 0 0 1 1 1V4h2a.75.75 0 0 1 0 1.5h-.54l-.63 7.31A1.75 1.75 0 0 1 10.08 14H5.92a1.75 1.75 0 0 1-1.74-1.19L3.54 5.5H3A.75.75 0 0 1 3 4h2v-.75a1 1 0 0 1 1-1Zm3.5 1.75V3.75h-3V4h3Zm-3.18 7.25a.75.75 0 1 0 1.5 0V7.25a.75.75 0 0 0-1.5 0v4Zm3.36 0a.75.75 0 1 0 1.5 0V7.25a.75.75 0 0 0-1.5 0v4Z"
                      fill="currentColor"
                    />
                  </svg>
                </button>
              </div>
            </td>
          </tr>
          <tr v-if="!displayedTransfers.length">
            <td class="empty-state" colspan="5">No history yet.</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
