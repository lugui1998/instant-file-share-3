<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import type { TransferRecord } from '../../agentBridge'
import HelpTooltip from '../settings/HelpTooltip.vue'
import PanelHeader from '../PanelHeader.vue'
import TablePagination from '../TablePagination.vue'

const props = defineProps<{
  transfers: TransferRecord[]
  itemsPerPage: number
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

function clampProgress(value: number) {
  return Math.max(0, Math.min(100, value))
}

function getProgressPercent(transfer: TransferRecord) {
  if (transfer.totalBytes <= 0) {
    return transfer.isActive ? 0 : 100
  }

  return clampProgress((transfer.bytesSent / transfer.totalBytes) * 100)
}

function formatBytes(value: number) {
  if (value < 1024) {
    return `${value} B`
  }

  if (value < 1024 * 1024) {
    return `${(value / 1024).toFixed(1)} KB`
  }

  if (value < 1024 * 1024 * 1024) {
    return `${(value / (1024 * 1024)).toFixed(1)} MB`
  }

  return `${(value / (1024 * 1024 * 1024)).toFixed(1)} GB`
}

function formatProgressLabel(transfer: TransferRecord) {
  const sent = formatBytes(transfer.bytesSent)
  const total = transfer.totalBytes > 0 ? formatBytes(transfer.totalBytes) : '?'
  return `${sent} / ${total}`
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
  return speed ? `${formatBytes(speed)}/s` : null
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
    return 'Completed'
  }

  return 'Failed / partial'
}

function getRemotePrimaryLabel(transfer: TransferRecord) {
  return transfer.requesterName ?? transfer.remoteAddress ?? 'n/a'
}

function isCrawlerTransfer(transfer: TransferRecord) {
  return Boolean(transfer.requesterName)
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
      <table>
        <thead>
          <tr>
            <th>File</th>
            <th>Remote</th>
            <th>Progress</th>
            <th>Status</th>
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
                <div class="progress-track" :aria-label="formatProgressLabel(transfer)" role="progressbar" :aria-valuemin="0" :aria-valuemax="100" :aria-valuenow="Math.round(getProgressPercent(transfer))">
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
          </tr>
          <tr v-if="!displayedTransfers.length">
            <td class="empty-state" colspan="4">No history yet.</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
