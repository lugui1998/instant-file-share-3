<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import type { TransferRecord } from '../../agentBridge'
import PanelHeader from '../PanelHeader.vue'

const props = defineProps<{
  transfers: TransferRecord[]
}>()

const pausedThresholdMs = 1500
const now = ref(Date.now())
const displayedTransfers = computed(() =>
  props.transfers.map((transfer) => ({
    ...transfer,
    isPaused:
      transfer.state === 'Paused' ||
      (transfer.isActive && now.value - new Date(transfer.lastUpdatedAtUtc).getTime() >= pausedThresholdMs),
  })),
)

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
</script>

<template>
  <section class="panel">
    <PanelHeader eyebrow="Transfers" title="" />

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
            <td>{{ transfer.remoteAddress ?? 'n/a' }}</td>
            <td class="progress-cell">
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
            </td>
            <td>
              <div class="status-copy">
                <strong>{{ getStatusLabel(transfer) }}</strong>
                <span v-if="transfer.error">{{ transfer.error }}</span>
              </div>
            </td>
          </tr>
          <tr v-if="!transfers.length">
            <td class="empty-state" colspan="4">No transfer events yet.</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
