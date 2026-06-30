<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
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
  buildManagedGzipUrl,
  chooseManagedCompressionMethod,
  createManagedCompressionProbeState,
  createRawCompressionSample,
  estimateBlobFallbackMemoryCost,
  formatTransferBitsPerSecond,
  formatTransferBytes,
  readManagedGzipResponseAsBuffer,
  recordManagedCompressionSample,
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
const activeControllers = ref<AbortController[]>([])
const isPaused = ref(false)
const compressionProbe = ref(createManagedCompressionProbeState())
const capabilities = ref({
  blobAssembly: true,
  gzipDecompression: false,
  streamingSave: false,
  webCrypto: false,
})

const defaultManagedDownloadRetryLimit = 8
const baseManagedDownloadRetryDelayMs = 1000
const maxManagedDownloadRetryDelayMs = 30000

const managedDownload = computed(() => props.file.managedDownload ?? null)
const maxManagedParallelChunks = computed(() => Math.max(1, managedDownload.value?.maxParallelChunks ?? 4))
const showTransferDiagnostics = computed(() => Boolean(managedDownload.value?.transferDiagnosticsEnabled))
const canAttemptCompressedDownload = computed(() =>
  managedDownload.value?.compressionMode === 'Auto' &&
  props.file.canUseBrowserCompression &&
  Boolean(props.file.compressedDownloadUrl))
const compressionDecision = computed(() => chooseManagedCompressionMethod({
  state: compressionProbe.value,
  canUseGzip: canAttemptCompressedDownload.value && capabilities.value.gzipDecompression,
}))
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
  const estimate = estimateBlobFallbackMemoryCost(Math.min(props.file.sizeBytes, managedDownload.value?.maxMemoryBytes ?? props.file.sizeBytes))
  return formatTransferBytes(estimate.minimumTransientBytes)
})
const managedMemoryLimitLabel = computed(() => formatTransferBytes(managedDownload.value?.maxMemoryBytes ?? 0))
const compressionBytesLabel = computed(() => {
  if (compressionProbe.value.gzipSampleCount === 0) {
    return 'not sampled'
  }

  const ratio = compressionProbe.value.gzipLogicalBytes > 0
    ? compressionProbe.value.gzipWireBytes / compressionProbe.value.gzipLogicalBytes
    : 1
  return `${formatTransferBytes(compressionProbe.value.gzipWireBytes)} wire / ${formatTransferBytes(compressionProbe.value.gzipLogicalBytes)} logical (${Math.round(ratio * 100)}%)`
})
const compressionSpeedLabel = computed(() => {
  const gzip = compressionProbe.value.lastGzipSample
  if (!gzip) {
    return 'not sampled'
  }

  const raw = compressionProbe.value.lastRawSample
  const gzipLabel = `${formatTransferBitsPerSecond(gzip.effectiveBytesPerSecond)} effective`
  return raw
    ? `${gzipLabel}, raw baseline ${formatTransferBitsPerSecond(raw.effectiveBytesPerSecond)}`
    : gzipLabel
})
const compressionDecodeLabel = computed(() => {
  if (compressionProbe.value.gzipSampleCount === 0) {
    return 'not sampled'
  }

  return `${Math.round(compressionProbe.value.gzipDecompressionDurationMs)} ms`
})
const managedUnavailableReason = computed(() => {
  if (!managedDownload.value) {
    return null
  }

  if (!capabilities.value.webCrypto) {
    return 'Browser-managed download needs Web Crypto to verify chunk integrity. Use Direct download.'
  }

  if (
    requiresStreamingManagedDownload(props.file.sizeBytes, managedDownload.value.maxMemoryBytes) &&
    !capabilities.value.streamingSave
  ) {
    return largeFileStreamingRequiredMessage
  }

  return null
})
const progressPercent = computed(() => {
  if (state.value.totalBytes <= 0) {
    return state.value.status === 'complete' ? 100 : 0
  }

  return Math.min(100, Math.round((state.value.downloadedBytes / state.value.totalBytes) * 100))
})
const canShowStart = computed(() => managedDownload.value !== null && ['idle', 'complete'].includes(state.value.status))
const canStart = computed(() => canShowStart.value && managedUnavailableReason.value === null)
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

onMounted(() => {
  capabilities.value = {
    blobAssembly: true,
    gzipDecompression: supportsGzipDecompression(),
    streamingSave: supportsStreamingFileSave(),
    webCrypto: typeof crypto !== 'undefined' && typeof crypto.subtle?.digest === 'function',
  }
})

async function startManagedDownload() {
  if (!managedDownload.value) {
    return
  }

  if (managedUnavailableReason.value) {
    state.value = reduceManagedDownloadState(state.value, { type: 'fail', error: managedUnavailableReason.value })
    return
  }

  isPaused.value = false
  chunks.value = []
  plan.value = null
  compressionProbe.value = createManagedCompressionProbeState()
  state.value = reduceManagedDownloadState(state.value, { type: 'start' })

  try {
    const nextPlan = await fetchManagedDownloadPlanWithRetry(managedDownload.value.manifestUrl)
    plan.value = nextPlan
    state.value = reduceManagedDownloadState(state.value, { type: 'plan-ready', totalBytes: nextPlan.fileSizeBytes })
    const useBlobAssembly = canUseBlobManagedDownload(nextPlan.fileSizeBytes, managedDownload.value.maxMemoryBytes)
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
    activeControllers.value = []
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
    if (requiresStreamingManagedDownload(plan.value.fileSizeBytes, managedDownload.value?.maxMemoryBytes)) {
      chunks.value = []
      await streamDownloadToDisk(plan.value)
    } else {
      await downloadMissingChunksToMemory(plan.value)
    }

    if (isPaused.value) {
      state.value = reduceManagedDownloadState(state.value, { type: 'pause' })
      return
    }

    if (canUseBlobManagedDownload(plan.value.fileSizeBytes, managedDownload.value?.maxMemoryBytes)) {
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
    activeControllers.value = []
  }
}

function pauseManagedDownload() {
  isPaused.value = true
  activeControllers.value.forEach((controller) => controller.abort())
  state.value = reduceManagedDownloadState(state.value, { type: 'pause' })
}

async function downloadMissingChunksToMemory(downloadPlan: BrowserManagedDownloadPlan) {
  let completedBytes = chunks.value.reduce((total, chunk) => total + (chunk?.size ?? 0), 0)
  const pendingChunks = downloadPlan.chunks.filter((chunk) => chunks.value[chunk.index] === null)
  let activeCount = 0
  let nextConcurrency = 1
  let successStreak = 0

  await new Promise<void>((resolve, reject) => {
    const pump = () => {
      if (isPaused.value) {
        resolve()
        return
      }

      if (pendingChunks.length === 0 && activeCount === 0) {
        resolve()
        return
      }

      while (!isPaused.value && activeCount < nextConcurrency && pendingChunks.length > 0) {
        const chunk = pendingChunks.shift()!
        activeCount += 1
        void fetchChunkWithRetry(downloadPlan, chunk)
          .then((blob) => {
            chunks.value[chunk.index] = blob
            completedBytes += blob.size
            successStreak += 1
            if (successStreak >= 2 && nextConcurrency < maxManagedParallelChunks.value) {
              nextConcurrency += 1
              successStreak = 0
            }
            state.value = reduceManagedDownloadState(state.value, {
              type: 'chunk-progress',
              chunkIndex: chunk.index,
              downloadedBytes: completedBytes,
            })
          })
          .catch((error) => {
            successStreak = 0
            nextConcurrency = Math.max(1, nextConcurrency - 1)
            reject(error)
          })
          .finally(() => {
            activeCount -= 1
            pump()
          })
      }
    }

    pump()
  })
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
  const retryLimit = resolveManagedDownloadRetryLimit(downloadPlan)
  for (let attempt = 0; attempt <= retryLimit; attempt++) {
    if (attempt > 0) {
      state.value = reduceManagedDownloadState(state.value, {
        type: 'chunk-retry',
        chunkIndex: chunk.index,
        retryCount: attempt,
      })
      await waitForManagedDownloadRetryDelay(attempt - 1)
      if (isPaused.value) {
        throw new DOMException('Download paused.', 'AbortError')
      }
    }

    try {
      return await fetchChunk(downloadPlan, chunk)
    } catch (error) {
      if (isPaused.value || attempt >= retryLimit || !isManagedDownloadRetryableError(error)) {
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
  const retryLimit = resolveManagedDownloadRetryLimit(downloadPlan)
  for (let attempt = 0; attempt <= retryLimit; attempt++) {
    if (attempt > 0) {
      state.value = reduceManagedDownloadState(state.value, {
        type: 'chunk-retry',
        chunkIndex: chunk.index,
        retryCount: attempt,
      })
      await waitForManagedDownloadRetryDelay(attempt - 1)
      if (isPaused.value) {
        throw new DOMException('Download paused.', 'AbortError')
      }
    }

    try {
      return await fetchVerifiedChunkBuffer(downloadPlan, chunk)
    } catch (error) {
      if (isPaused.value || attempt >= retryLimit || !isManagedDownloadRetryableError(error)) {
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

  if (compressionDecision.value.method === 'gzip') {
    return await fetchCompressedVerifiedChunkBuffer(downloadPlan, chunk)
  }

  return await fetchRawVerifiedChunkBuffer(downloadPlan, chunk)
}

async function fetchRawVerifiedChunkBuffer(downloadPlan: BrowserManagedDownloadPlan, chunk: BrowserManagedDownloadChunk) {
  const controller = new AbortController()
  activeControllers.value.push(controller)
  const transferStart = readTimestamp()
  try {
    const response = await fetch(downloadPlan.rawDownloadUrl, {
      headers: {
        Range: `bytes=${chunk.start}-${chunk.end}`,
      },
      signal: controller.signal,
      cache: 'no-store',
    })

    if (response.status !== 206 && !(chunk.sizeBytes === downloadPlan.fileSizeBytes && response.ok)) {
      throw new Error(`Chunk ${chunk.index} returned HTTP ${response.status}.`)
    }

    const buffer = await response.arrayBuffer()
    const transferDurationMs = readTimestamp() - transferStart
    if (buffer.byteLength !== chunk.sizeBytes) {
      throw new Error(`Chunk ${chunk.index} returned ${buffer.byteLength} bytes instead of ${chunk.sizeBytes}.`)
    }

    const actualHash = await sha256Hex(buffer)
    if (actualHash !== chunk.sha256) {
      throwChunkIntegrityError(chunk.index)
    }

    compressionProbe.value = recordManagedCompressionSample(
      compressionProbe.value,
      createRawCompressionSample(buffer.byteLength, transferDurationMs),
    )
    return buffer
  } finally {
    activeControllers.value = activeControllers.value.filter((entry) => entry !== controller)
  }
}

async function fetchCompressedVerifiedChunkBuffer(downloadPlan: BrowserManagedDownloadPlan, chunk: BrowserManagedDownloadChunk) {
  const controller = new AbortController()
  activeControllers.value.push(controller)
  try {
    const response = await fetch(buildManagedGzipUrl(downloadPlan.rawDownloadUrl), {
      headers: {
        Range: `bytes=${chunk.start}-${chunk.end}`,
      },
      signal: controller.signal,
      cache: 'no-store',
    })
    const { buffer, sample } = await readManagedGzipResponseAsBuffer(response, chunk.sizeBytes)

    const actualHash = await sha256Hex(buffer)
    if (actualHash !== chunk.sha256) {
      throwChunkIntegrityError(chunk.index)
    }

    compressionProbe.value = recordManagedCompressionSample(compressionProbe.value, sample)
    return buffer
  } catch (error) {
    compressionProbe.value = recordManagedCompressionSample(compressionProbe.value, {
      method: 'gzip',
      logicalBytes: chunk.sizeBytes,
      wireBytes: 0,
      transferDurationMs: 0,
      decompressionDurationMs: 0,
      ok: false,
    })
    throw error
  } finally {
    activeControllers.value = activeControllers.value.filter((entry) => entry !== controller)
  }
}

function throwChunkIntegrityError(chunkIndex: number): never {
  throw new Error(`Chunk ${chunkIndex} failed integrity verification. The shared file may have changed after the download plan was created. Restart the download or use Direct download.`)
}

async function fetchManagedDownloadPlanWithRetry(manifestUrl: string) {
  for (let attempt = 0; attempt <= defaultManagedDownloadRetryLimit; attempt += 1) {
    if (attempt > 0) {
      await waitForManagedDownloadRetryDelay(attempt - 1)
    }

    try {
      const manifestResponse = await fetch(manifestUrl, { cache: 'no-store' })
      if (!manifestResponse.ok) {
        throw new Error(`Download plan failed with HTTP ${manifestResponse.status}.`)
      }

      return await manifestResponse.json() as BrowserManagedDownloadPlan
    } catch (error) {
      if (attempt >= defaultManagedDownloadRetryLimit || !isManagedDownloadRetryableError(error)) {
        throw error
      }
    }
  }

  throw new Error('Download plan failed.')
}

function resolveManagedDownloadRetryLimit(downloadPlan: BrowserManagedDownloadPlan) {
  return Math.max(defaultManagedDownloadRetryLimit, Math.max(0, downloadPlan.maxRetriesPerChunk))
}

function waitForManagedDownloadRetryDelay(attempt: number) {
  const delayMs = Math.min(maxManagedDownloadRetryDelayMs, baseManagedDownloadRetryDelayMs * 2 ** Math.min(attempt, 5))
  return new Promise<void>((resolve) => {
    window.setTimeout(resolve, delayMs)
  })
}

function isManagedDownloadRetryableError(error: unknown) {
  if (error instanceof DOMException && error.name === 'AbortError') {
    return false
  }

  const message = error instanceof Error ? error.message : ''
  if (message.includes('failed integrity verification') ||
    message.includes('shared file may have changed') ||
    message.includes('HTTP 400') ||
    message.includes('HTTP 401') ||
    message.includes('HTTP 403') ||
    message.includes('HTTP 404') ||
    message.includes('HTTP 409') ||
    message.includes('HTTP 410') ||
    message.includes('HTTP 416')) {
    return false
  }

  return true
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

function readTimestamp() {
  return globalThis.performance?.now?.() ?? Date.now()
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
        {{ managedUnavailableReason ?? compressionSupportLabel }}
        <template v-if="canAttemptCompressedDownload && !hasStreamingFileSave">
          - Blob path buffers about {{ blobMemoryLabel }} before saving.
        </template>
      </p>

      <dl v-if="showTransferDiagnostics" class="transfer-diagnostics">
        <div>
          <dt>Method</dt>
          <dd>{{ requiresStreamingManagedDownload(file.sizeBytes, managedDownload.maxMemoryBytes) ? 'streaming save' : 'Blob assembly' }}</dd>
        </div>
        <div>
          <dt>Concurrency</dt>
          <dd>adaptive 1-{{ maxManagedParallelChunks }}</dd>
        </div>
        <div>
          <dt>Memory limit</dt>
          <dd>{{ managedMemoryLimitLabel }}</dd>
        </div>
        <div>
          <dt>Compression</dt>
          <dd>{{ compressionDecision.method }} - {{ compressionDecision.reason }}</dd>
        </div>
        <div>
          <dt>Compressed bytes</dt>
          <dd>{{ compressionBytesLabel }}</dd>
        </div>
        <div>
          <dt>Compression speed</dt>
          <dd>{{ compressionSpeedLabel }}</dd>
        </div>
        <div>
          <dt>Decompression</dt>
          <dd>{{ compressionDecodeLabel }}</dd>
        </div>
        <div>
          <dt>Capabilities</dt>
          <dd>
            stream {{ capabilities.streamingSave ? 'yes' : 'no' }},
            gzip {{ capabilities.gzipDecompression ? 'yes' : 'no' }},
            crypto {{ capabilities.webCrypto ? 'yes' : 'no' }}
          </dd>
        </div>
        <div v-if="plan">
          <dt>Plan</dt>
          <dd>{{ plan.chunks.length }} chunks, {{ formatTransferBytes(plan.chunkSizeBytes) }} each</dd>
        </div>
        <div>
          <dt>Retries</dt>
          <dd>{{ state.retryCount }}</dd>
        </div>
      </dl>

      <p v-if="state.error" class="download-error">{{ state.error }}</p>

      <div class="download-actions">
        <button
          v-if="canShowStart"
          class="button button--primary"
          type="button"
          :disabled="!canStart"
          @click="startManagedDownload"
        >
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
