<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  canPauseManagedDownload,
  canResumeManagedDownload,
  canUseBlobManagedDownload,
  createManagedDownloadState,
  largeFileStreamingRequiredMessage,
  reduceManagedDownloadState,
  requiresStreamingManagedDownload,
} from '../../downloadState'
import {
  estimateBlobFallbackMemoryCost,
  formatTransferBytes,
  supportsGzipDecompression,
  supportsStreamingFileSave,
} from '../../compressionDownload'
import type {
  BrowserManagedDownloadChunk,
  BrowserManagedDownloadPlan,
  PublicShareFileModel,
  PublicSharePageModel,
} from '../../types'

const props = defineProps<{
  page: PublicSharePageModel
  file: PublicShareFileModel
}>()

const state = ref(createManagedDownloadState())
const plan = ref<BrowserManagedDownloadPlan | null>(null)
const chunks = ref<Array<Blob | null>>([])
const activeController = ref<AbortController | null>(null)
const isPaused = ref(false)

const managedDownload = computed(() => props.file.managedDownload ?? null)
const canAttemptCompressedDownload = computed(() => props.file.canUseBrowserCompression && Boolean(props.file.compressedDownloadUrl))
const hasGzipDecompression = computed(() => supportsGzipDecompression())
const hasStreamingFileSave = computed(() => supportsStreamingFileSave())
const compressionSupportLabel = computed(() => {
  if (!canAttemptCompressedDownload.value) {
    return 'Raw transfer'
  }

  if (!hasGzipDecompression.value) {
    return 'Raw transfer fallback'
  }

  return hasStreamingFileSave.value ? 'Managed gzip stream available' : 'Managed gzip Blob fallback available'
})
const blobMemoryLabel = computed(() => {
  const estimate = estimateBlobFallbackMemoryCost(props.file.sizeBytes)
  return formatTransferBytes(estimate.minimumTransientBytes)
})
const progressPercent = computed(() => {
  if (state.value.totalBytes <= 0) {
    return state.value.status === 'complete' ? 100 : 0
  }

  return Math.min(100, Math.round((state.value.downloadedBytes / state.value.totalBytes) * 100))
})
const canStart = computed(() => managedDownload.value !== null && ['idle', 'complete'].includes(state.value.status))
const canPause = computed(() => canPauseManagedDownload(state.value))
const canResume = computed(() => canResumeManagedDownload(state.value))
const statusLabel = computed(() => {
  switch (state.value.status) {
    case 'planning':
      return 'Preparing'
    case 'downloading':
      return `Downloading ${progressPercent.value}%`
    case 'paused':
      return `Paused at ${progressPercent.value}%`
    case 'saving':
      return 'Saving'
    case 'complete':
      return 'Complete'
    case 'failed':
      return 'Failed'
    case 'idle':
    default:
      return 'Ready'
  }
})

async function startManagedDownload() {
  if (!managedDownload.value) {
    return
  }

  isPaused.value = false
  chunks.value = []
  plan.value = null
  state.value = reduceManagedDownloadState(state.value, { type: 'start' })

  try {
    const manifestResponse = await fetch(managedDownload.value.manifestUrl, { cache: 'no-store' })
    if (!manifestResponse.ok) {
      throw new Error(`Download plan failed with HTTP ${manifestResponse.status}.`)
    }

    const nextPlan = await manifestResponse.json() as BrowserManagedDownloadPlan
    plan.value = nextPlan
    state.value = reduceManagedDownloadState(state.value, { type: 'plan-ready', totalBytes: nextPlan.fileSizeBytes })
    const useBlobAssembly = canUseBlobManagedDownload(nextPlan.fileSizeBytes)
    chunks.value = useBlobAssembly ? Array.from({ length: nextPlan.chunks.length }, () => null) : []

    if (useBlobAssembly) {
      await downloadMissingChunksToMemory(nextPlan)
    } else {
      await streamDownloadToDisk(nextPlan)
    }

    if (isPaused.value) {
      state.value = reduceManagedDownloadState(state.value, { type: 'pause' })
      return
    }

    if (useBlobAssembly) {
      state.value = reduceManagedDownloadState(state.value, { type: 'saving' })
      saveBlob(new Blob(chunks.value.filter((chunk): chunk is Blob => chunk !== null), { type: nextPlan.contentType }), nextPlan.fileName)
    }
    state.value = reduceManagedDownloadState(state.value, { type: 'complete' })
  } catch (error) {
    if (isPaused.value) {
      state.value = reduceManagedDownloadState(state.value, { type: 'pause' })
      return
    }

    state.value = reduceManagedDownloadState(state.value, {
      type: 'fail',
      error: error instanceof Error ? error.message : 'The download failed.',
    })
  } finally {
    activeController.value = null
  }
}

async function resumeManagedDownload() {
  if (!plan.value) {
    await startManagedDownload()
    return
  }

  isPaused.value = false
  state.value = reduceManagedDownloadState(state.value, { type: 'resume' })

  try {
    if (requiresStreamingManagedDownload(plan.value.fileSizeBytes)) {
      chunks.value = []
      await streamDownloadToDisk(plan.value)
    } else {
      await downloadMissingChunksToMemory(plan.value)
    }

    if (isPaused.value) {
      state.value = reduceManagedDownloadState(state.value, { type: 'pause' })
      return
    }

    if (canUseBlobManagedDownload(plan.value.fileSizeBytes)) {
      state.value = reduceManagedDownloadState(state.value, { type: 'saving' })
      saveBlob(new Blob(chunks.value.filter((chunk): chunk is Blob => chunk !== null), { type: plan.value.contentType }), plan.value.fileName)
    }
    state.value = reduceManagedDownloadState(state.value, { type: 'complete' })
  } catch (error) {
    if (isPaused.value) {
      state.value = reduceManagedDownloadState(state.value, { type: 'pause' })
      return
    }

    state.value = reduceManagedDownloadState(state.value, {
      type: 'fail',
      error: error instanceof Error ? error.message : 'The download failed.',
    })
  } finally {
    activeController.value = null
  }
}

function pauseManagedDownload() {
  isPaused.value = true
  activeController.value?.abort()
  state.value = reduceManagedDownloadState(state.value, { type: 'pause' })
}

async function downloadMissingChunksToMemory(downloadPlan: BrowserManagedDownloadPlan) {
  let completedBytes = chunks.value.reduce((total, chunk) => total + (chunk?.size ?? 0), 0)
  for (const chunk of downloadPlan.chunks) {
    if (isPaused.value) {
      return
    }

    if (chunks.value[chunk.index] !== null) {
      continue
    }

    const blob = await fetchChunkWithRetry(downloadPlan, chunk)
    chunks.value[chunk.index] = blob
    completedBytes += blob.size
    state.value = reduceManagedDownloadState(state.value, {
      type: 'chunk-progress',
      chunkIndex: chunk.index,
      downloadedBytes: completedBytes,
    })
  }
}

async function streamDownloadToDisk(downloadPlan: BrowserManagedDownloadPlan) {
  const picker = window.showSaveFilePicker
  if (typeof picker !== 'function') {
    throw new Error(largeFileStreamingRequiredMessage)
  }

  const handle = await picker({
    suggestedName: downloadPlan.fileName,
  })
  const writable = await handle.createWritable()
  let completedBytes = 0

  try {
    for (const chunk of downloadPlan.chunks) {
      if (isPaused.value) {
        await writable.abort()
        return
      }

      const buffer = await fetchVerifiedChunkBufferWithRetry(downloadPlan, chunk)
      await writable.write(buffer)
      completedBytes += buffer.byteLength
      state.value = reduceManagedDownloadState(state.value, {
        type: 'chunk-progress',
        chunkIndex: chunk.index,
        downloadedBytes: completedBytes,
      })
    }

    state.value = reduceManagedDownloadState(state.value, { type: 'saving' })
    await writable.close()
  } catch (error) {
    await writable.abort().catch(() => {})
    throw error
  }
}

async function fetchChunkWithRetry(downloadPlan: BrowserManagedDownloadPlan, chunk: BrowserManagedDownloadChunk) {
  for (let attempt = 0; attempt <= downloadPlan.maxRetriesPerChunk; attempt++) {
    if (attempt > 0) {
      state.value = reduceManagedDownloadState(state.value, {
        type: 'chunk-retry',
        chunkIndex: chunk.index,
        retryCount: attempt,
      })
    }

    try {
      return await fetchChunk(downloadPlan, chunk)
    } catch (error) {
      if (isPaused.value || attempt >= downloadPlan.maxRetriesPerChunk) {
        throw error
      }
    }
  }

  throw new Error(`Chunk ${chunk.index} failed.`)
}

async function fetchChunk(downloadPlan: BrowserManagedDownloadPlan, chunk: BrowserManagedDownloadChunk) {
  const buffer = await fetchVerifiedChunkBuffer(downloadPlan, chunk)
  return new Blob([buffer], { type: downloadPlan.contentType })
}

async function fetchVerifiedChunkBufferWithRetry(downloadPlan: BrowserManagedDownloadPlan, chunk: BrowserManagedDownloadChunk) {
  for (let attempt = 0; attempt <= downloadPlan.maxRetriesPerChunk; attempt++) {
    if (attempt > 0) {
      state.value = reduceManagedDownloadState(state.value, {
        type: 'chunk-retry',
        chunkIndex: chunk.index,
        retryCount: attempt,
      })
    }

    try {
      return await fetchVerifiedChunkBuffer(downloadPlan, chunk)
    } catch (error) {
      if (isPaused.value || attempt >= downloadPlan.maxRetriesPerChunk) {
        throw error
      }
    }
  }

  throw new Error(`Chunk ${chunk.index} failed.`)
}

async function fetchVerifiedChunkBuffer(downloadPlan: BrowserManagedDownloadPlan, chunk: BrowserManagedDownloadChunk) {
  if (chunk.sizeBytes === 0) {
    const emptyBuffer = new ArrayBuffer(0)
    const actualHash = await sha256Hex(emptyBuffer)
    if (actualHash !== chunk.sha256) {
      throwChunkIntegrityError(chunk.index)
    }

    return emptyBuffer
  }

  activeController.value = new AbortController()
  const response = await fetch(downloadPlan.rawDownloadUrl, {
    headers: {
      Range: `bytes=${chunk.start}-${chunk.end}`,
    },
    signal: activeController.value.signal,
    cache: 'no-store',
  })

  if (response.status !== 206 && !(chunk.sizeBytes === downloadPlan.fileSizeBytes && response.ok)) {
    throw new Error(`Chunk ${chunk.index} returned HTTP ${response.status}.`)
  }

  const buffer = await response.arrayBuffer()
  if (buffer.byteLength !== chunk.sizeBytes) {
    throw new Error(`Chunk ${chunk.index} returned ${buffer.byteLength} bytes instead of ${chunk.sizeBytes}.`)
  }

  const actualHash = await sha256Hex(buffer)
  if (actualHash !== chunk.sha256) {
    throwChunkIntegrityError(chunk.index)
  }

  return buffer
}

function throwChunkIntegrityError(chunkIndex: number): never {
  throw new Error(`Chunk ${chunkIndex} failed integrity verification. The shared file may have changed after the download plan was created. Restart the download or use Direct download.`)
}

async function sha256Hex(buffer: ArrayBuffer) {
  const hash = await crypto.subtle.digest('SHA-256', buffer)
  return Array.from(new Uint8Array(hash))
    .map((byte) => byte.toString(16).padStart(2, '0'))
    .join('')
}

function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.rel = 'noreferrer'
  document.body.appendChild(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}
</script>

<template>
  <section class="stack">
    <div class="hero-panel">
      <div>
        <p class="label">File</p>
        <h2>{{ file.fileName }}</h2>
      </div>

      <span class="badge">{{ file.displaySize }}</span>
    </div>

    <p class="body-copy">{{ file.actionLabel }}</p>

    <div v-if="managedDownload" class="download-panel">
      <div class="download-panel__header">
        <div>
          <span class="label">Browser download</span>
          <strong>{{ statusLabel }}</strong>
        </div>

        <span class="badge">{{ progressPercent }}%</span>
      </div>

      <div
        class="download-progress"
        role="progressbar"
        :aria-valuenow="progressPercent"
        aria-valuemin="0"
        aria-valuemax="100"
      >
        <span :style="{ width: `${progressPercent}%` }" />
      </div>

      <p class="download-note" role="status">
        {{ compressionSupportLabel }}
        <template v-if="canAttemptCompressedDownload && !hasStreamingFileSave">
          - Blob path buffers about {{ blobMemoryLabel }} before saving.
        </template>
      </p>

      <p v-if="state.error" class="download-error">{{ state.error }}</p>

      <div class="download-actions">
        <button v-if="canStart" class="button button--primary" type="button" @click="startManagedDownload">
          Download in browser
        </button>
        <button v-if="canPause" class="button" type="button" @click="pauseManagedDownload">
          Pause
        </button>
        <button v-if="canResume" class="button button--primary" type="button" @click="resumeManagedDownload">
          {{ state.status === 'failed' ? 'Retry' : 'Resume' }}
        </button>
        <a class="button button--ghost" :href="managedDownload.rawDownloadUrl">
          Direct download
        </a>
      </div>
    </div>

    <div v-else class="download-actions">
      <a class="public-shell__action" :href="file.rawDownloadUrl">
        {{ file.actionVerb }} file
      </a>
    </div>

    <div class="info-grid">
      <article class="info-card">
        <span class="label">Mode</span>
        <strong>{{ file.preferInline ? 'Open in browser when supported' : 'Download only' }}</strong>
      </article>

      <article class="info-card">
        <span class="label">Content type</span>
        <strong>{{ canAttemptCompressedDownload ? 'Raw or browser-decompressed gzip' : file.actionVerb + ' from this link' }}</strong>
      </article>
    </div>
  </section>
</template>
