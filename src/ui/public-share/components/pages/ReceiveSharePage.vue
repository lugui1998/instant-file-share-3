<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import {
  base64UrlEncode,
  bytesToArrayBuffer,
  createBrowserTransferNoncePrefix,
  encryptBrowserTransferChunk,
  importBrowserTransferKey,
  maxBrowserTransferBufferedBytes,
  parseBrowserTransferKeyFragment,
} from '../../crypto/browserTransferCrypto'
import type {
  PublicReceiveUploadResponse,
  PublicSharePageModel,
  PublicShareReceiveModel,
} from '../../types'

type UploadState = 'queued' | 'uploading' | 'saving' | 'success' | 'error' | 'canceled'

type UploadEntry = {
  id: string
  uploadId: string
  batchId: string
  file: File
  relativePath: string
  progress: number
  receiveProgress: number | null
  receivedBytes: number
  receiveTotalBytes: number
  receiveSpeedBytesPerSecond: number | null
  receiveLastUpdatedAtMs: number | null
  state: UploadState
  message: string
  uploadedBytes: number
  uploadStartedAtMs: number | null
  speedBytesPerSecond: number | null
  request?: XMLHttpRequest | null
  socket?: WebSocket | null
  recommendedChunkSizeBytes?: number | null
  autoTransportScores?: Record<ReceiveUploadTransport, AutoUploadTransportScore>
}

type ReceiveUploadTransport = 'MultipartChunks' | 'BinaryChunks' | 'WebSocket'

type AutoUploadTransportScore = {
  successes: number
  failures: number
  bytes: number
  durationMs: number
  receiveBytes: number
  receiveDurationMs: number
}

type UploadListItem =
  | { type: 'file'; key: string; entry: UploadEntry }
  | {
      type: 'folder'
      key: string
      label: string
      entries: UploadEntry[]
      progress: number
      receiveProgress: number | null
      state: UploadState
      expanded: boolean
    }

const props = defineProps<{
  page: PublicSharePageModel
  receive: PublicShareReceiveModel
}>()

const defaultParallelUploadLimit = 4
const defaultUploadChunkSizeBytes = 16 * 1024 * 1024
const defaultUploadMaxBodySizeBytes = 95 * 1024 * 1024
const minimumUploadChunkSizeBytes = 1024 * 1024
const receiveProgressUpdateIntervalMs = 250
const autoUploadTransports: ReceiveUploadTransport[] = ['BinaryChunks', 'WebSocket', 'MultipartChunks']

const emit = defineEmits<{
  quotaLabelChange: [label: string]
}>()

const uploadEntries = ref<UploadEntry[]>([])
const isDragActive = ref(false)
const activeUploadCount = ref(0)
const summaryMessage = ref('')
const filePicker = ref<HTMLInputElement | null>(null)
const expandedFolders = ref<Set<string>>(new Set())
const currentBatchId = ref<string | null>(null)
const completedBatchIds = ref<Set<string>>(new Set())
const dragDepth = ref(0)
const pendingReceiveProgressEvents = new Map<string, ReceiveUploadProgressEvent>()
let uploadEventsSocket: WebSocket | null = null
let receiveProgressFlushTimer: ReturnType<typeof setTimeout> | null = null

type ReceiveUploadProgressEvent = {
  uploadId: string
  receivedBytes: number
  totalBytes: number
  state: string
  succeeded: boolean
  error?: string | null
  recommendedChunkSizeBytes?: number | null
}

const totalBytes = computed(() => uploadEntries.value.reduce((sum, entry) => sum + entry.file.size, 0))
const completedBytes = computed(() =>
  uploadEntries.value.reduce((sum, entry) => sum + entry.file.size * (entry.progress / 100), 0),
)
const hostReceivedBytes = computed(() => uploadEntries.value.reduce((sum, entry) => sum + entry.receivedBytes, 0))
const hasOverallReceiveProgress = computed(() => uploadEntries.value.some((entry) => entry.receiveProgress !== null))
const showOverallProgress = computed(() => uploadEntries.value.length > 1)
const overallProgress = computed(() => totalBytes.value > 0 ? Math.min(100, (completedBytes.value / totalBytes.value) * 100) : 0)
const overallReceiveProgress = computed(() => totalBytes.value > 0 ? Math.min(100, (hostReceivedBytes.value / totalBytes.value) * 100) : 0)
const overallDisplayProgress = computed(() => hasOverallReceiveProgress.value ? overallReceiveProgress.value : overallProgress.value)
const parallelUploadLimit = computed(() => normalizeParallelUploadLimit(props.receive.parallelUploadLimit))
const uploadChunkSizeBytes = computed(() => normalizeUploadChunkSizeBytes(props.receive.uploadChunkSizeBytes))
const uploadMaxBodySizeBytes = computed(() => normalizeUploadMaxBodySizeBytes(props.receive.uploadMaxBodySizeBytes))
const uploadPacketSizeBytes = computed(() => Math.min(uploadChunkSizeBytes.value, uploadMaxBodySizeBytes.value))
const encryptionFragment = computed(() =>
  props.receive.encryptionExperiment ? parseBrowserTransferKeyFragment(window.location.hash) : null)
const uploadListItems = computed<UploadListItem[]>(() => {
  const items: UploadListItem[] = []
  const folderItems = new Map<string, UploadEntry[]>()
  const folderOrder: string[] = []

  for (const entry of uploadEntries.value) {
    const folderName = getTopLevelFolder(entry.relativePath)
    if (!folderName) {
      items.push({ type: 'file', key: entry.id, entry })
      continue
    }

    if (!folderItems.has(folderName)) {
      folderItems.set(folderName, [])
      folderOrder.push(folderName)
    }

    folderItems.get(folderName)!.push(entry)
  }

  for (const folderName of folderOrder) {
    const entries = folderItems.get(folderName)!
    items.push({
      type: 'folder',
      key: `folder:${folderName}`,
      label: folderName,
      entries,
      progress: calculateGroupProgress(entries),
      receiveProgress: calculateGroupReceiveProgress(entries),
      state: calculateGroupState(entries),
      expanded: expandedFolders.value.has(folderName),
    })
  }

  return items
})

function createUploadEntries(files: Array<{ file: File; relativePath: string }>, batchId: string) {
  const createdAt = Date.now()
  return files.map((entry, index) => ({
    id: `${createdAt}-${index}-${entry.relativePath}`,
    uploadId: createUploadId(),
    batchId,
    file: entry.file,
    relativePath: entry.relativePath,
    progress: 0,
    receiveProgress: null,
    receivedBytes: 0,
    receiveTotalBytes: entry.file.size,
    receiveSpeedBytesPerSecond: null,
    receiveLastUpdatedAtMs: null,
    state: 'queued' as UploadState,
    message: '',
    uploadedBytes: 0,
    uploadStartedAtMs: null,
    speedBytesPerSecond: null,
    request: null,
    socket: null,
    recommendedChunkSizeBytes: null,
    autoTransportScores: undefined,
  }))
}

function addUploadEntries(files: Array<{ file: File; relativePath: string }>) {
  if (!files.length) {
    return
  }

  const batchId = resolveCurrentBatchId()
  uploadEntries.value.push(...createUploadEntries(files, batchId))
  summaryMessage.value = ''
  void startUploadQueue()
}

function queueLooseFiles(fileList: FileList | null) {
  if (!fileList?.length) {
    return
  }

  addUploadEntries(
    Array.from(fileList, (file) => ({
      file,
      relativePath: file.webkitRelativePath || file.name,
    })),
  )
}

async function queueDroppedItems(event: DragEvent) {
  const items = Array.from(event.dataTransfer?.items ?? [])
  if (!items.length) {
    queueLooseFiles(event.dataTransfer?.files ?? null)
    return
  }

  const collectedFiles = (await Promise.all(items.map(collectDroppedFiles))).flat()
  if (collectedFiles.length === 0) {
    queueLooseFiles(event.dataTransfer?.files ?? null)
    return
  }

  addUploadEntries(collectedFiles)
}

function openFilePicker() {
  filePicker.value?.click()
}

function handleFilePicker(event: Event) {
  const input = event.target as HTMLInputElement
  queueLooseFiles(input.files)
  input.value = ''
}

function resolveCurrentBatchId() {
  if (currentBatchId.value && hasActiveBatchEntries(currentBatchId.value)) {
    return currentBatchId.value
  }

  const batchId = createBatchId()
  currentBatchId.value = batchId
  completedBatchIds.value.delete(batchId)
  return batchId
}

function createBatchId() {
  return createUploadId()
}

function createUploadId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

function hasActiveBatchEntries(batchId: string) {
  return uploadEntries.value.some((entry) =>
    entry.batchId === batchId &&
    (entry.state === 'queued' || entry.state === 'uploading' || entry.state === 'saving'))
}

function startUploadQueue() {
  while (activeUploadCount.value < parallelUploadLimit.value) {
    const nextEntry = uploadEntries.value.find((entry) => entry.state === 'queued')
    if (!nextEntry) {
      return
    }

    activeUploadCount.value += 1
    void uploadEntry(nextEntry).finally(() => {
      activeUploadCount.value = Math.max(0, activeUploadCount.value - 1)
      startUploadQueue()
      completeBatchIfFinished(nextEntry.batchId)
    })
  }
}

async function uploadEntry(entry: UploadEntry) {
  entry.uploadId = createUploadId()
  entry.progress = 0
  entry.receiveProgress = null
  entry.receivedBytes = 0
  entry.receiveTotalBytes = entry.file.size
  entry.receiveSpeedBytesPerSecond = null
  entry.receiveLastUpdatedAtMs = null
  entry.recommendedChunkSizeBytes = null
  entry.autoTransportScores = undefined
  entry.state = 'uploading'
  entry.message = 'Uploading...'
  entry.uploadedBytes = 0
  entry.uploadStartedAtMs = Date.now()
  entry.speedBytesPerSecond = null

  try {
    const response = await sendUploadForEntry(entry)
    applyUploadResult(entry, response)
  } catch (cause) {
    if (isCanceled(entry)) {
      return
    }

    entry.state = 'error'
    entry.message = cause instanceof Error ? cause.message : 'Upload failed.'
    summaryMessage.value = entry.message
  } finally {
    entry.request = null
    entry.socket = null
  }
}

function sendUploadForEntry(entry: UploadEntry) {
  if (props.receive.encryptionExperiment && encryptionFragment.value && encryptionFragment.value.mode !== 'download') {
    return sendEncryptedStoreUploadRequest(entry)
  }

  if (props.receive.uploadMode === 'CompressedStream') {
    return sendCompressedStreamUploadRequest(entry)
  }

  if (props.receive.uploadMode === 'Auto') {
    return sendAutoChunkedUploadRequest(entry)
  }

  if (props.receive.uploadMode === 'WebSocket') {
    return sendWebSocketChunkedUploadRequest(entry)
  }

  if (entry.file.size <= resolveNextChunkSizeBytes(entry)) {
    return sendUploadRequest(entry, createUploadFormData(entry, entry.file))
  }

  return props.receive.uploadMode === 'BinaryChunks' || props.receive.uploadMode === 'AdaptiveBinaryChunks'
    ? sendBinaryChunkedUploadRequest(entry)
    : sendChunkedUploadRequest(entry)
}

async function sendAutoChunkedUploadRequest(entry: UploadEntry) {
  entry.autoTransportScores = createAutoTransportScores()
  let finalResponse: PublicReceiveUploadResponse | null = null
  let chunkStart = 0
  let acceptedChunkCount = 0
  const failedTransportsAtBoundary = new Set<ReceiveUploadTransport>()

  while (chunkStart < entry.file.size) {
    const confirmedStart = resolveConfirmedChunkStart(entry, chunkStart)
    if (confirmedStart > chunkStart) {
      updateEntryUploadProgress(entry, confirmedStart)
      chunkStart = confirmedStart
      acceptedChunkCount += 1
      failedTransportsAtBoundary.clear()
    }

    const chunkSizeBytes = resolveNextChunkSizeBytes(entry)
    const chunkEnd = Math.min(entry.file.size, chunkStart + chunkSizeBytes)
    const chunkSize = chunkEnd - chunkStart
    const chunkIndex = acceptedChunkCount
    const transport = chooseAutoUploadTransport(entry, chunkIndex, failedTransportsAtBoundary)
    const startedAtMs = Date.now()
    const receivedBytesAtStart = entry.receivedBytes
    const receiveUpdatedAtStartMs = entry.receiveLastUpdatedAtMs

    try {
      finalResponse = await sendChunkWithTransport(
        entry,
        transport,
        chunkIndex,
        resolveChunkCountForRequest(entry.file.size, chunkStart, chunkSize, chunkSizeBytes, chunkIndex),
        chunkStart,
        chunkSizeBytes,
      )
      assertUploadResponseSucceeded(finalResponse)
      recordAutoTransportSuccess(entry, transport, chunkSize, Date.now() - startedAtMs, receivedBytesAtStart, receiveUpdatedAtStartMs)

      const nextChunkStart = Math.max(chunkEnd, resolveConfirmedChunkStart(entry, chunkEnd))
      updateEntryUploadProgress(entry, nextChunkStart)
      chunkStart = nextChunkStart
      acceptedChunkCount += 1
      failedTransportsAtBoundary.clear()
    } catch (cause) {
      recordAutoTransportFailure(entry, transport)
      failedTransportsAtBoundary.add(transport)

      const confirmedBoundary = resolveConfirmedChunkStart(entry, chunkStart)
      if (confirmedBoundary > chunkStart) {
        updateEntryUploadProgress(entry, confirmedBoundary)
        chunkStart = confirmedBoundary
        acceptedChunkCount += 1
        failedTransportsAtBoundary.clear()
        continue
      }

      if (failedTransportsAtBoundary.size >= autoUploadTransports.length) {
        throw cause
      }
    }
  }

  if (!finalResponse || finalResponse.results.length === 0) {
    finalResponse = createConfirmedUploadResponse(entry)
  }

  return finalResponse
}

function createConfirmedUploadResponse(entry: UploadEntry): PublicReceiveUploadResponse {
  return {
    uploadedCount: 1,
    failedCount: 0,
    remainingQuotaBytes: props.receive.remainingQuotaBytes,
    results: [
      {
        success: true,
        message: null,
        sizeBytes: entry.file.size,
      },
    ],
  }
}

function sendChunkWithTransport(
  entry: UploadEntry,
  transport: ReceiveUploadTransport,
  chunkIndex: number,
  chunkCount: number,
  chunkStart: number,
  chunkSizeBytes: number,
) {
  if (transport === 'BinaryChunks') {
    const chunkEnd = Math.min(entry.file.size, chunkStart + chunkSizeBytes)
    return sendBinaryChunkUploadRequest(entry, entry.file.slice(chunkStart, chunkEnd), chunkIndex, chunkCount, chunkStart)
  }

  if (transport === 'WebSocket') {
    return sendWebSocketChunkUploadRequest(entry, chunkIndex, chunkCount, chunkStart, chunkSizeBytes)
  }

  return sendUploadRequest(
    entry,
    createChunkUploadFormData(entry, entry.uploadId, chunkIndex, chunkCount, chunkStart, chunkSizeBytes),
    chunkStart,
  )
}

function createAutoTransportScores(): Record<ReceiveUploadTransport, AutoUploadTransportScore> {
  return {
    MultipartChunks: createAutoTransportScore(),
    BinaryChunks: createAutoTransportScore(),
    WebSocket: createAutoTransportScore(),
  }
}

function createAutoTransportScore(): AutoUploadTransportScore {
  return {
    successes: 0,
    failures: 0,
    bytes: 0,
    durationMs: 0,
    receiveBytes: 0,
    receiveDurationMs: 0,
  }
}

function chooseAutoUploadTransport(
  entry: UploadEntry,
  chunkIndex: number,
  failedTransportsAtBoundary: Set<ReceiveUploadTransport>,
): ReceiveUploadTransport {
  if (failedTransportsAtBoundary.size >= autoUploadTransports.length - 1 && !failedTransportsAtBoundary.has('MultipartChunks')) {
    return 'MultipartChunks'
  }

  const probeChunkCount = normalizeAutoProbeChunkCount(props.receive.uploadAutoProbeChunkCount)
  if (chunkIndex < probeChunkCount) {
    const probeTransport = chooseAutoProbeTransport(chunkIndex, failedTransportsAtBoundary)
    if (probeTransport) {
      return probeTransport
    }
  }

  const scores = entry.autoTransportScores ?? createAutoTransportScores()
  let bestTransport: ReceiveUploadTransport = 'MultipartChunks'
  let bestScore = Number.NEGATIVE_INFINITY
  for (const transport of autoUploadTransports) {
    if (failedTransportsAtBoundary.has(transport)) {
      continue
    }

    const score = calculateAutoTransportScore(scores[transport], transport)
    if (score > bestScore) {
      bestScore = score
      bestTransport = transport
    }
  }

  return bestTransport
}

function chooseAutoProbeTransport(
  chunkIndex: number,
  failedTransportsAtBoundary: Set<ReceiveUploadTransport>,
) {
  for (let offset = 0; offset < autoUploadTransports.length; offset += 1) {
    const transport = autoUploadTransports[(chunkIndex + offset) % autoUploadTransports.length]
    if (!failedTransportsAtBoundary.has(transport)) {
      return transport
    }
  }

  return null
}

function calculateAutoTransportScore(score: AutoUploadTransportScore, transport: ReceiveUploadTransport) {
  const measuredDurationMs = score.receiveDurationMs > 0 ? score.receiveDurationMs : score.durationMs
  const measuredBytes = score.receiveDurationMs > 0 ? score.receiveBytes : score.bytes
  const bytesPerSecond = measuredDurationMs > 0 ? measuredBytes / (measuredDurationMs / 1000) : 0
  const conservativeBias = transport === 'MultipartChunks' ? 100 : 0
  return score.successes * 1000 - score.failures * 5000 + conservativeBias + Math.min(bytesPerSecond / 1024, 1000)
}

function recordAutoTransportSuccess(
  entry: UploadEntry,
  transport: ReceiveUploadTransport,
  bytes: number,
  durationMs: number,
  receivedBytesAtStart: number,
  receiveUpdatedAtStartMs: number | null,
) {
  const score = getAutoTransportScore(entry, transport)
  score.successes += 1
  score.bytes += Math.max(0, bytes)
  score.durationMs += Math.max(1, durationMs)

  if (entry.receiveLastUpdatedAtMs !== null && receiveUpdatedAtStartMs !== null) {
    const receiveDurationMs = entry.receiveLastUpdatedAtMs - receiveUpdatedAtStartMs
    const receiveBytes = entry.receivedBytes - receivedBytesAtStart
    if (receiveDurationMs > 0 && receiveBytes > 0) {
      score.receiveBytes += receiveBytes
      score.receiveDurationMs += receiveDurationMs
    }
  }
}

function recordAutoTransportFailure(entry: UploadEntry, transport: ReceiveUploadTransport) {
  const score = getAutoTransportScore(entry, transport)
  score.failures += 1
}

function getAutoTransportScore(entry: UploadEntry, transport: ReceiveUploadTransport) {
  entry.autoTransportScores ??= createAutoTransportScores()
  return entry.autoTransportScores[transport]
}

function resolveConfirmedChunkStart(entry: UploadEntry, fallback: number) {
  return Math.max(fallback, Math.min(entry.file.size, entry.receivedBytes))
}

async function sendEncryptedStoreUploadRequest(entry: UploadEntry) {
  const fragment = encryptionFragment.value
  if (!fragment) {
    throw new Error('Encrypted upload key is missing.')
  }

  if (entry.file.size > maxBrowserTransferBufferedBytes) {
    throw new Error('Encrypted browser uploads are limited to 64 MB because this experiment buffers the full file before encryption.')
  }

  entry.message = 'Encrypting...'
  const key = await importBrowserTransferKey(fragment.keyBytes)
  const noncePrefix = createBrowserTransferNoncePrefix()
  const encrypted = await encryptBrowserTransferChunk(key, await readBlobAsArrayBuffer(entry.file), noncePrefix, 0)
  const ciphertext = new Blob([bytesToArrayBuffer(encrypted.ciphertext)], { type: 'application/octet-stream' })

  return sendEncryptedBinaryUploadRequest(entry, ciphertext, encrypted.iv)
}

function readBlobAsArrayBuffer(blob: Blob) {
  if (typeof blob.arrayBuffer === 'function') {
    return blob.arrayBuffer()
  }

  return new Promise<ArrayBuffer>((resolve, reject) => {
    const reader = new FileReader()
    reader.addEventListener('load', () => resolve(reader.result as ArrayBuffer))
    reader.addEventListener('error', () => reject(reader.error ?? new Error('File read failed.')))
    reader.readAsArrayBuffer(blob)
  })
}

function sendEncryptedBinaryUploadRequest(
  entry: UploadEntry,
  ciphertext: Blob,
  iv: Uint8Array,
) {
  return new Promise<PublicReceiveUploadResponse>((resolve, reject) => {
    const request = new XMLHttpRequest()
    entry.request = request
    request.open('POST', props.receive.uploadUrl, true)
    request.responseType = 'json'
    request.setRequestHeader('Content-Type', 'application/octet-stream')
    request.setRequestHeader('X-IFS-Upload-Id', entry.uploadId)
    request.setRequestHeader('X-IFS-Batch-Id', entry.batchId)
    request.setRequestHeader('X-IFS-Relative-Path', encodeURIComponent(entry.relativePath))
    request.setRequestHeader('X-IFS-File-Name', encodeURIComponent(entry.file.name))
    request.setRequestHeader('X-IFS-File-Size', ciphertext.size.toString())
    request.setRequestHeader('X-IFS-Chunk-Index', '0')
    request.setRequestHeader('X-IFS-Chunk-Count', '1')
    request.setRequestHeader('X-IFS-Chunk-Start', '0')
    request.setRequestHeader('X-IFS-Chunk-Size', ciphertext.size.toString())
    request.setRequestHeader('X-IFS-Encryption-Mode', 'store-encrypted')
    request.setRequestHeader('X-IFS-Encryption-Algorithm', 'AES-GCM')
    request.setRequestHeader('X-IFS-Encryption-IV', base64UrlEncode(iv))
    request.setRequestHeader('X-IFS-Plaintext-File-Size', entry.file.size.toString())

    request.upload.addEventListener('progress', (event) => {
      if (!event.lengthComputable || entry.state !== 'uploading') {
        return
      }

      const uploadedBytes = Math.min(entry.file.size, Math.round(entry.file.size * (event.loaded / Math.max(1, event.total))))
      updateEntryUploadProgress(entry, uploadedBytes)
    })

    request.addEventListener('load', () => {
      const response = request.response as PublicReceiveUploadResponse | null

      if (request.status < 200 || request.status >= 300) {
        reject(new Error(getUploadFailureMessage(response) || `Upload failed with status ${request.status}.`))
        return
      }

      if (!response) {
        reject(new Error('Upload failed.'))
        return
      }

      resolve(response)
    })

    request.addEventListener('abort', () => {
      reject(new Error('Upload stopped.'))
    })

    request.addEventListener('error', () => {
      reject(new Error('Upload failed.'))
    })

    request.send(ciphertext)
  })
}

function createUploadFormData(entry: UploadEntry, file: Blob) {
  const formData = new FormData()
  formData.append('relativePaths', entry.relativePath)
  formData.append('batchId', entry.batchId)
  formData.append('fileSizes', entry.file.size.toString())
  formData.append('uploadId', entry.uploadId)
  formData.append('files', file, entry.file.name)
  return formData
}

function createChunkUploadFormData(
  entry: UploadEntry,
  uploadId: string,
  chunkIndex: number,
  chunkCount: number,
  chunkStart: number,
  chunkSizeBytes: number,
) {
  const chunkEnd = Math.min(entry.file.size, chunkStart + chunkSizeBytes)
  const chunk = entry.file.slice(chunkStart, chunkEnd)
  const formData = new FormData()
  formData.append('relativePaths', entry.relativePath)
  formData.append('batchId', entry.batchId)
  formData.append('fileSizes', entry.file.size.toString())
  formData.append('uploadId', uploadId)
  formData.append('chunkIndex', chunkIndex.toString())
  formData.append('chunkCount', chunkCount.toString())
  formData.append('chunkStart', chunkStart.toString())
  formData.append('chunkSize', chunk.size.toString())
  formData.append('files', chunk, entry.file.name)
  return formData
}

async function sendChunkedUploadRequest(entry: UploadEntry) {
  let finalResponse: PublicReceiveUploadResponse | null = null
  let chunkStart = 0
  let chunkIndex = 0

  while (chunkStart < entry.file.size) {
    const chunkSizeBytes = resolveNextChunkSizeBytes(entry)
    const chunkEnd = Math.min(entry.file.size, chunkStart + chunkSizeBytes)
    finalResponse = await sendUploadRequest(
      entry,
      createChunkUploadFormData(
        entry,
        entry.uploadId,
        chunkIndex,
        resolveChunkCountForRequest(entry.file.size, chunkStart, chunkEnd - chunkStart, chunkSizeBytes, chunkIndex),
        chunkStart,
        chunkSizeBytes,
      ),
      chunkStart,
    )
    assertUploadResponseSucceeded(finalResponse)

    updateEntryUploadProgress(entry, chunkEnd)
    chunkStart = chunkEnd
    chunkIndex += 1
  }

  if (!finalResponse) {
    throw new Error('Upload failed.')
  }

  return finalResponse
}

async function sendBinaryChunkedUploadRequest(entry: UploadEntry) {
  let finalResponse: PublicReceiveUploadResponse | null = null
  let chunkStart = 0
  let chunkIndex = 0

  while (chunkStart < entry.file.size) {
    const chunkSizeBytes = resolveNextChunkSizeBytes(entry)
    const chunkEnd = Math.min(entry.file.size, chunkStart + chunkSizeBytes)
    const chunk = entry.file.slice(chunkStart, chunkEnd)
    finalResponse = await sendBinaryChunkUploadRequest(
      entry,
      chunk,
      chunkIndex,
      resolveChunkCountForRequest(entry.file.size, chunkStart, chunk.size, chunkSizeBytes, chunkIndex),
      chunkStart,
    )
    assertUploadResponseSucceeded(finalResponse)
    updateEntryUploadProgress(entry, chunkEnd)
    chunkStart = chunkEnd
    chunkIndex += 1
  }

  if (!finalResponse) {
    throw new Error('Upload failed.')
  }

  return finalResponse
}

async function sendCompressedStreamUploadRequest(entry: UploadEntry) {
  const compressedBlob = await createGzipBlob(entry.file)
  return sendCompressedStreamRequest(entry, compressedBlob)
}

async function createGzipBlob(file: File) {
  if (typeof CompressionStream === 'undefined') {
    throw new Error('Compressed uploads are not supported by this browser.')
  }

  const compressedStream = file.stream().pipeThrough(new CompressionStream('gzip'))
  return await new Response(compressedStream).blob()
}

async function sendWebSocketChunkedUploadRequest(entry: UploadEntry) {
  const socket = await openUploadSocket(entry)
  let finalResponse: PublicReceiveUploadResponse | null = null
  let chunkStart = 0
  let chunkIndex = 0

  try {
    while (chunkStart < entry.file.size) {
      const chunkSizeBytes = resolveNextChunkSizeBytes(entry)
      const chunkEnd = Math.min(entry.file.size, chunkStart + chunkSizeBytes)
      const chunk = entry.file.slice(chunkStart, chunkEnd)
      socket.send(JSON.stringify({
        type: 'chunk',
        uploadId: entry.uploadId,
        batchId: entry.batchId,
        relativePath: entry.relativePath,
        fileName: entry.file.name,
        fileSize: entry.file.size,
        chunkIndex,
        chunkCount: resolveChunkCountForRequest(entry.file.size, chunkStart, chunk.size, chunkSizeBytes, chunkIndex),
        chunkStart,
        chunkSize: chunk.size,
      }))
      socket.send(chunk)
      finalResponse = await waitForUploadSocketResponse(socket)
      assertUploadResponseSucceeded(finalResponse)
      updateEntryUploadProgress(entry, chunkEnd)
      chunkStart = chunkEnd
      chunkIndex += 1
    }
  } finally {
    socket.close()
    if (entry.socket === socket) {
      entry.socket = null
    }
  }

  if (!finalResponse) {
    throw new Error('Upload failed.')
  }

  return finalResponse
}

async function sendWebSocketChunkUploadRequest(
  entry: UploadEntry,
  chunkIndex: number,
  chunkCount: number,
  chunkStart: number,
  chunkSizeBytes: number,
) {
  const socket = await openUploadSocket(entry)
  try {
    const chunkEnd = Math.min(entry.file.size, chunkStart + chunkSizeBytes)
    const chunk = entry.file.slice(chunkStart, chunkEnd)
    socket.send(JSON.stringify({
      type: 'chunk',
      uploadId: entry.uploadId,
      batchId: entry.batchId,
      relativePath: entry.relativePath,
      fileName: entry.file.name,
      fileSize: entry.file.size,
      chunkIndex,
      chunkCount,
      chunkStart,
      chunkSize: chunk.size,
    }))
    socket.send(chunk)
    return await waitForUploadSocketResponse(socket)
  } finally {
    socket.close()
    if (entry.socket === socket) {
      entry.socket = null
    }
  }
}

function assertUploadResponseSucceeded(response: PublicReceiveUploadResponse) {
  const failureMessage = getUploadFailureMessage(response)
  if (response.failedCount > 0 || failureMessage) {
    throw new Error(failureMessage || 'Upload failed.')
  }
}

function getUploadFailureMessage(response: PublicReceiveUploadResponse | null | undefined) {
  const failedResult = response?.results.find((result) => !result.success)
  const message = failedResult?.message?.trim()
  return message ? message : null
}

function resolveNextChunkSizeBytes(entry: UploadEntry) {
  if (props.receive.uploadChunkSizingMode !== 'Auto' && props.receive.uploadMode !== 'AdaptiveBinaryChunks') {
    return uploadPacketSizeBytes.value
  }

  return Math.min(
    normalizeUploadChunkSizeBytes(entry.recommendedChunkSizeBytes ?? minimumUploadChunkSizeBytes),
    uploadMaxBodySizeBytes.value,
  )
}

function resolveChunkCountForRequest(
  fileSize: number,
  chunkStart: number,
  chunkSize: number,
  currentChunkSizeBytes: number,
  chunkIndex: number,
) {
  const chunkEnd = chunkStart + chunkSize
  if (chunkEnd >= fileSize) {
    return chunkIndex + 1
  }

  if (props.receive.uploadChunkSizingMode === 'Auto' || props.receive.uploadMode === 'AdaptiveBinaryChunks') {
    return 2147483647
  }

  return Math.ceil(fileSize / currentChunkSizeBytes)
}

function openUploadSocket(entry: UploadEntry) {
  return new Promise<WebSocket>((resolve, reject) => {
    if (!props.receive.uploadSocketUrl) {
      reject(new Error('Upload socket is not available.'))
      return
    }

    const eventsUrl = new URL(props.receive.uploadSocketUrl, window.location.href)
    eventsUrl.protocol = eventsUrl.protocol === 'https:' ? 'wss:' : 'ws:'
    const socket = new WebSocket(eventsUrl.toString())
    entry.socket = socket
    socket.addEventListener('open', () => resolve(socket), { once: true })
    socket.addEventListener('error', () => reject(new Error('Upload failed.')), { once: true })
  })
}

function waitForUploadSocketResponse(socket: WebSocket) {
  return new Promise<PublicReceiveUploadResponse>((resolve, reject) => {
    const cleanup = () => {
      socket.removeEventListener('message', handleMessage)
      socket.removeEventListener('error', handleError)
      socket.removeEventListener('close', handleClose)
    }
    const handleMessage = (event: MessageEvent) => {
      cleanup()
      try {
        resolve(JSON.parse(event.data as string) as PublicReceiveUploadResponse)
      } catch {
        reject(new Error('Upload failed.'))
      }
    }
    const handleError = () => {
      cleanup()
      reject(new Error('Upload failed.'))
    }
    const handleClose = () => {
      cleanup()
      reject(new Error('Upload stopped.'))
    }

    socket.addEventListener('message', handleMessage)
    socket.addEventListener('error', handleError)
    socket.addEventListener('close', handleClose)
  })
}

function sendUploadRequest(entry: UploadEntry, formData: FormData, uploadedBytesOffset = 0) {
  return new Promise<PublicReceiveUploadResponse>((resolve, reject) => {
    const request = new XMLHttpRequest()
    entry.request = request
    request.open('POST', props.receive.uploadUrl, true)
    request.responseType = 'json'

    request.upload.addEventListener('progress', (event) => {
      if (!event.lengthComputable || entry.state !== 'uploading') {
        return
      }

      const uploadedBytes = Math.min(entry.file.size, uploadedBytesOffset + event.loaded)
      updateEntryUploadProgress(entry, uploadedBytes)
    })

    request.addEventListener('load', () => {
      const response = request.response as PublicReceiveUploadResponse | null

      if (request.status < 200 || request.status >= 300) {
        reject(new Error(getUploadFailureMessage(response) || `Upload failed with status ${request.status}.`))
        return
      }

      if (!response) {
        reject(new Error('Upload failed.'))
        return
      }

      resolve(response)
    })

    request.addEventListener('abort', () => {
      reject(new Error('Upload stopped.'))
    })

    request.addEventListener('error', () => {
      reject(new Error('Upload failed.'))
    })

    request.send(formData)
  })
}

function sendBinaryChunkUploadRequest(
  entry: UploadEntry,
  chunk: Blob,
  chunkIndex: number,
  chunkCount: number,
  chunkStart: number,
) {
  return new Promise<PublicReceiveUploadResponse>((resolve, reject) => {
    const request = new XMLHttpRequest()
    entry.request = request
    request.open('POST', props.receive.uploadUrl, true)
    request.responseType = 'json'
    request.setRequestHeader('Content-Type', 'application/octet-stream')
    request.setRequestHeader('X-IFS-Upload-Id', entry.uploadId)
    request.setRequestHeader('X-IFS-Batch-Id', entry.batchId)
    request.setRequestHeader('X-IFS-Relative-Path', encodeURIComponent(entry.relativePath))
    request.setRequestHeader('X-IFS-File-Name', encodeURIComponent(entry.file.name))
    request.setRequestHeader('X-IFS-File-Size', entry.file.size.toString())
    request.setRequestHeader('X-IFS-Chunk-Index', chunkIndex.toString())
    request.setRequestHeader('X-IFS-Chunk-Count', chunkCount.toString())
    request.setRequestHeader('X-IFS-Chunk-Start', chunkStart.toString())
    request.setRequestHeader('X-IFS-Chunk-Size', chunk.size.toString())

    request.upload.addEventListener('progress', (event) => {
      if (!event.lengthComputable || entry.state !== 'uploading') {
        return
      }

      const uploadedBytes = Math.min(entry.file.size, chunkStart + event.loaded)
      updateEntryUploadProgress(entry, uploadedBytes)
    })

    request.addEventListener('load', () => {
      const response = request.response as PublicReceiveUploadResponse | null

      if (request.status < 200 || request.status >= 300) {
        reject(new Error(getUploadFailureMessage(response) || `Upload failed with status ${request.status}.`))
        return
      }

      if (!response) {
        reject(new Error('Upload failed.'))
        return
      }

      resolve(response)
    })

    request.addEventListener('abort', () => {
      reject(new Error('Upload stopped.'))
    })

    request.addEventListener('error', () => {
      reject(new Error('Upload failed.'))
    })

    request.send(chunk)
  })
}

function sendCompressedStreamRequest(entry: UploadEntry, compressedBlob: Blob) {
  return new Promise<PublicReceiveUploadResponse>((resolve, reject) => {
    const request = new XMLHttpRequest()
    entry.request = request
    request.open('POST', props.receive.uploadUrl, true)
    request.responseType = 'json'
    request.setRequestHeader('Content-Type', 'application/gzip')
    request.setRequestHeader('X-IFS-Upload-Id', entry.uploadId)
    request.setRequestHeader('X-IFS-Batch-Id', entry.batchId)
    request.setRequestHeader('X-IFS-Relative-Path', encodeURIComponent(entry.relativePath))
    request.setRequestHeader('X-IFS-File-Name', encodeURIComponent(entry.file.name))
    request.setRequestHeader('X-IFS-File-Size', entry.file.size.toString())

    request.upload.addEventListener('progress', (event) => {
      if (!event.lengthComputable || entry.state !== 'uploading') {
        return
      }

      const totalWireBytes = Math.max(1, event.total || compressedBlob.size)
      const logicalBytes = Math.min(entry.file.size, entry.file.size * (event.loaded / totalWireBytes))
      updateEntryUploadProgress(entry, logicalBytes)
    })

    request.addEventListener('load', () => {
      const response = request.response as PublicReceiveUploadResponse | null

      if (request.status < 200 || request.status >= 300) {
        reject(new Error(getUploadFailureMessage(response) || `Upload failed with status ${request.status}.`))
        return
      }

      if (!response) {
        reject(new Error('Upload failed.'))
        return
      }

      resolve(response)
    })

    request.addEventListener('abort', () => {
      reject(new Error('Upload stopped.'))
    })

    request.addEventListener('error', () => {
      reject(new Error('Upload failed.'))
    })

    request.send(compressedBlob)
  })
}

function updateEntryUploadProgress(entry: UploadEntry, uploadedBytes: number) {
  if (entry.state !== 'uploading') {
    return
  }

  const clampedUploadedBytes = Math.min(entry.file.size, uploadedBytes)
  const progress = Math.min(100, entry.file.size > 0 ? (clampedUploadedBytes / entry.file.size) * 100 : 100)
  entry.progress = progress
  entry.uploadedBytes = clampedUploadedBytes
  entry.speedBytesPerSecond = calculateUploadSpeed(entry, clampedUploadedBytes)
  if (progress >= 100) {
    entry.state = 'saving'
    entry.message = 'Saving...'
    entry.speedBytesPerSecond = null
  }
}

function applyUploadResult(entry: UploadEntry, response: PublicReceiveUploadResponse) {
  emit('quotaLabelChange', props.receive.remainingQuotaLabel === 'Unlimited' ? 'Unlimited' : formatBytes(response.remainingQuotaBytes))
  const result = response.results[0]

  if (!result) {
    entry.state = 'error'
    entry.message = 'No server result was returned for this file.'
    summaryMessage.value = entry.message
    return
  }

  entry.progress = 100
  entry.uploadedBytes = entry.file.size
  entry.speedBytesPerSecond = null
  entry.state = result.success ? 'success' : 'error'
  entry.message = result.success ? '' : (result.message || 'Upload failed.')

  if (!result.success) {
    summaryMessage.value = entry.message
  }
}

function stopEntry(entry: UploadEntry) {
  if (entry.state !== 'uploading' && entry.state !== 'queued') {
    return
  }

  entry.state = 'canceled'
  entry.message = 'Stopped'
  entry.speedBytesPerSecond = null
  entry.receiveSpeedBytesPerSecond = null
  void sendUploadCancelRequest(entry)
  entry.request?.abort()
  entry.socket?.close()
}

function restartEntry(entry: UploadEntry) {
  if (entry.state === 'uploading' || entry.state === 'queued') {
    return
  }

  entry.progress = 0
  entry.uploadId = createUploadId()
  entry.receiveProgress = null
  entry.receivedBytes = 0
  entry.receiveTotalBytes = entry.file.size
  entry.receiveSpeedBytesPerSecond = null
  entry.receiveLastUpdatedAtMs = null
  entry.recommendedChunkSizeBytes = null
  entry.state = 'queued'
  entry.message = ''
  entry.uploadedBytes = 0
  entry.uploadStartedAtMs = null
  entry.speedBytesPerSecond = null
  entry.batchId = resolveCurrentBatchId()
  summaryMessage.value = ''
  void startUploadQueue()
}

function stopGroup(entries: UploadEntry[]) {
  for (const entry of entries) {
    stopEntry(entry)
  }
}

function restartGroup(entries: UploadEntry[]) {
  for (const entry of entries) {
    if (canRestartEntry(entry)) {
      restartEntry(entry)
    }
  }
}

function completeBatchIfFinished(batchId: string) {
  if (completedBatchIds.value.has(batchId) || hasActiveBatchEntries(batchId)) {
    return
  }

  completedBatchIds.value = new Set(completedBatchIds.value).add(batchId)
  if (currentBatchId.value === batchId) {
    currentBatchId.value = null
  }

  if (uploadEntries.value.some((entry) => entry.batchId === batchId && entry.state === 'success')) {
    void sendBatchCompletionRequest(batchId)
  }
}

function sendBatchCompletionRequest(batchId: string) {
  return new Promise<void>((resolve) => {
    const request = new XMLHttpRequest()
    request.open('POST', `${props.receive.uploadUrl}?ifs=batch-complete&batchId=${encodeURIComponent(batchId)}`, true)
    request.addEventListener('loadend', () => resolve())
    request.addEventListener('error', () => resolve())
    request.send()
  })
}

async function sendUploadCancelRequest(entry: UploadEntry) {
  if (!entry.uploadId) {
    return
  }

  const cancelUrl = new URL(`${props.receive.uploadUrl.replace(/\/$/, '')}/cancel-upload`, window.location.href)
  cancelUrl.searchParams.set('uploadId', entry.uploadId)

  if (typeof fetch === 'function') {
    try {
      await fetch(cancelUrl.toString(), { method: 'POST', keepalive: true })
      return
    } catch {
    }
  }

  try {
    const request = new XMLHttpRequest()
    request.open('POST', cancelUrl.toString(), true)
    request.send()
  } catch {
  }
}

function toggleFolder(folderName: string) {
  const nextExpandedFolders = new Set(expandedFolders.value)
  if (nextExpandedFolders.has(folderName)) {
    nextExpandedFolders.delete(folderName)
  } else {
    nextExpandedFolders.add(folderName)
  }

  expandedFolders.value = nextExpandedFolders
}

function handleGroupAction(item: Extract<UploadListItem, { type: 'folder' }>) {
  if (canStopGroup(item.entries)) {
    stopGroup(item.entries)
    return
  }

  restartGroup(item.entries)
}

function handleWindowDragEnter(event: DragEvent) {
  if (!isFileDragEvent(event)) {
    return
  }

  event.preventDefault()
  dragDepth.value += 1
  isDragActive.value = true
}

function handleWindowDragOver(event: DragEvent) {
  if (!isFileDragEvent(event)) {
    return
  }

  event.preventDefault()
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = 'copy'
  }

  isDragActive.value = true
}

function handleWindowDragLeave(event: DragEvent) {
  if (!isFileDragEvent(event)) {
    return
  }

  dragDepth.value = Math.max(0, dragDepth.value - 1)
  if (dragDepth.value === 0 || isLeavingWindow(event)) {
    isDragActive.value = false
  }
}

async function handleWindowDrop(event: DragEvent) {
  if (!isFileDragEvent(event)) {
    return
  }

  event.preventDefault()
  dragDepth.value = 0
  isDragActive.value = false
  await queueDroppedItems(event)
}

function isFileDragEvent(event: DragEvent) {
  const dataTransfer = event.dataTransfer
  if (!dataTransfer) {
    return false
  }

  return Array.from(dataTransfer.types ?? []).includes('Files') ||
    (dataTransfer.files?.length ?? 0) > 0 ||
    Array.from(dataTransfer.items ?? []).some((item) => item.kind === 'file')
}

function isLeavingWindow(event: DragEvent) {
  return event.clientX <= 0 ||
    event.clientY <= 0 ||
    event.clientX >= window.innerWidth ||
    event.clientY >= window.innerHeight
}

function formatProgress(progress: number) {
  return `${Math.round(progress)}%`
}

function formatBytes(value: number) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let normalized = Math.max(0, value)
  let unitIndex = 0

  while (normalized >= 1024 && unitIndex < units.length - 1) {
    normalized /= 1024
    unitIndex++
  }

  return unitIndex === 0 ? `${Math.round(normalized)} ${units[unitIndex]}` : `${normalized.toFixed(1).replace(/\.0$/, '')} ${units[unitIndex]}`
}

function formatUploadSpeed(bytesPerSecond: number) {
  return `${formatBytes(bytesPerSecond)}/s`
}

function normalizeParallelUploadLimit(value: number | null | undefined) {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return defaultParallelUploadLimit
  }

  const normalized = Math.max(0, Math.round(value))
  return normalized === 0 ? Number.POSITIVE_INFINITY : normalized
}

function normalizeUploadChunkSizeBytes(value: number | null | undefined) {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return defaultUploadChunkSizeBytes
  }

  return Math.max(minimumUploadChunkSizeBytes, Math.round(value))
}

function normalizeUploadMaxBodySizeBytes(value: number | null | undefined) {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return defaultUploadMaxBodySizeBytes
  }

  return Math.max(minimumUploadChunkSizeBytes, Math.round(value))
}

function normalizeAutoProbeChunkCount(value: number | null | undefined) {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 4
  }

  return Math.max(1, Math.round(value))
}

function calculateUploadSpeed(entry: UploadEntry, loadedBytes: number) {
  if (!entry.uploadStartedAtMs) {
    return null
  }

  const elapsedSeconds = (Date.now() - entry.uploadStartedAtMs) / 1000
  return elapsedSeconds > 0 && loadedBytes > 0 ? loadedBytes / elapsedSeconds : null
}

function getTopLevelFolder(relativePath: string) {
  const normalizedPath = relativePath.replaceAll('\\', '/')
  const separatorIndex = normalizedPath.indexOf('/')
  return separatorIndex > 0 ? normalizedPath.slice(0, separatorIndex) : null
}

function calculateGroupProgress(entries: UploadEntry[]) {
  const groupBytes = entries.reduce((sum, entry) => sum + entry.file.size, 0)
  if (groupBytes <= 0) {
    return entries.every((entry) => entry.state === 'success') ? 100 : 0
  }

  const uploadedBytes = entries.reduce((sum, entry) => sum + entry.file.size * (entry.progress / 100), 0)
  return Math.min(100, (uploadedBytes / groupBytes) * 100)
}

function calculateGroupSize(entries: UploadEntry[]) {
  return entries.reduce((sum, entry) => sum + entry.file.size, 0)
}

function calculateGroupReceiveProgress(entries: UploadEntry[]) {
  if (!entries.some((entry) => entry.receiveProgress !== null)) {
    return null
  }

  const groupBytes = entries.reduce((sum, entry) => sum + entry.file.size, 0)
  if (groupBytes <= 0) {
    return 0
  }

  const receivedBytes = entries.reduce((sum, entry) => sum + entry.receivedBytes, 0)
  return Math.min(100, (receivedBytes / groupBytes) * 100)
}

function hasReceiveProgress(entry: UploadEntry) {
  return entry.receiveProgress !== null
}

function getReceiveProgress(entry: UploadEntry) {
  return entry.receiveProgress ?? 0
}

function getDisplayProgress(entry: UploadEntry) {
  return entry.receiveProgress ?? entry.progress
}

function shouldShowEntryMessage(entry: UploadEntry) {
  return Boolean(entry.message) && entry.state !== 'saving'
}

function hasGroupReceiveProgress(entries: UploadEntry[]) {
  return entries.some((entry) => entry.receiveProgress !== null)
}

function getGroupDisplayProgress(item: Extract<UploadListItem, { type: 'folder' }>) {
  return item.receiveProgress ?? item.progress
}

function calculateGroupState(entries: UploadEntry[]): UploadState {
  if (entries.some((entry) => entry.state === 'uploading')) {
    return 'uploading'
  }

  if (entries.some((entry) => entry.state === 'saving')) {
    return 'saving'
  }

  if (entries.some((entry) => entry.state === 'queued')) {
    return 'queued'
  }

  if (entries.some((entry) => entry.state === 'error')) {
    return 'error'
  }

  if (entries.some((entry) => entry.state === 'canceled')) {
    return 'canceled'
  }

  return 'success'
}

function canStopEntry(entry: UploadEntry) {
  return entry.state === 'uploading' || entry.state === 'queued'
}

function canRestartEntry(entry: UploadEntry) {
  return entry.state === 'error' || entry.state === 'canceled'
}

function isCanceled(entry: UploadEntry) {
  return entry.state === 'canceled'
}

function canStopGroup(entries: UploadEntry[]) {
  return entries.some(canStopEntry)
}

function canRestartGroup(entries: UploadEntry[]) {
  return entries.some(canRestartEntry)
}

function getEntryActionLabel(entry: UploadEntry) {
  if (canStopEntry(entry)) {
    return 'Stop'
  }

  return canRestartEntry(entry) ? 'Restart' : ''
}

function getEntrySpeedLabel(entry: UploadEntry) {
  if (entry.receiveProgress !== null) {
    return entry.receiveSpeedBytesPerSecond ? formatUploadSpeed(entry.receiveSpeedBytesPerSecond) : ''
  }

  return entry.state === 'uploading' && entry.speedBytesPerSecond
    ? formatUploadSpeed(entry.speedBytesPerSecond)
    : ''
}

function getGroupActionLabel(entries: UploadEntry[]) {
  if (canStopGroup(entries)) {
    return 'Stop'
  }

  return canRestartGroup(entries) ? 'Restart' : ''
}

function getGroupSpeedLabel(entries: UploadEntry[]) {
  if (entries.some((entry) => entry.receiveProgress !== null)) {
    const receiveBytesPerSecond = entries.reduce((sum, entry) => {
      if (entry.state === 'success' || entry.state === 'error' || entry.state === 'canceled') {
        return sum
      }

      return sum + (entry.receiveSpeedBytesPerSecond ?? 0)
    }, 0)
    return receiveBytesPerSecond > 0 ? formatUploadSpeed(receiveBytesPerSecond) : ''
  }

  const bytesPerSecond = entries.reduce((sum, entry) => sum + (entry.state === 'uploading' ? entry.speedBytesPerSecond ?? 0 : 0), 0)
  return bytesPerSecond > 0 ? formatUploadSpeed(bytesPerSecond) : ''
}

async function collectDroppedFiles(item: DataTransferItem): Promise<Array<{ file: File; relativePath: string }>> {
  const entry = (item as DataTransferItem & { webkitGetAsEntry?: () => any }).webkitGetAsEntry?.()
  if (!entry) {
    const file = item.getAsFile()
    return file ? [{ file, relativePath: file.name }] : []
  }

  return readEntry(entry, '')
}

async function readEntry(entry: any, parentPath: string): Promise<Array<{ file: File; relativePath: string }>> {
  if (entry.isFile) {
    const file = await new Promise<File>((resolve, reject) => entry.file(resolve, reject))
    return [{ file, relativePath: `${parentPath}${entry.name}` }]
  }

  if (!entry.isDirectory) {
    return []
  }

  const reader = entry.createReader()
  const children: any[] = []
  while (true) {
    const batch = await new Promise<any[]>((resolve, reject) => reader.readEntries(resolve, reject))
    if (batch.length === 0) {
      break
    }

    children.push(...batch)
  }

  const childFiles = await Promise.all(children.map((child) => readEntry(child, `${parentPath}${entry.name}/`)))
  return childFiles.flat()
}

function connectUploadEvents() {
  if (!props.receive.uploadEventsUrl || typeof WebSocket === 'undefined') {
    return
  }

  try {
    const eventsUrl = new URL(props.receive.uploadEventsUrl, window.location.href)
    eventsUrl.protocol = eventsUrl.protocol === 'https:' ? 'wss:' : 'ws:'
    uploadEventsSocket = new WebSocket(eventsUrl.toString())
    uploadEventsSocket.addEventListener('message', (event) => {
      if (typeof event.data !== 'string') {
        return
      }

      try {
        queueReceiveProgress(JSON.parse(event.data) as ReceiveUploadProgressEvent)
      } catch {
      }
    })
  } catch {
  }
}

function queueReceiveProgress(event: ReceiveUploadProgressEvent) {
  const entry = uploadEntries.value.find((candidate) => candidate.uploadId === event.uploadId)
  applyReceiveProgressRecommendation(entry, event)
  if (entry?.state === 'canceled') {
    return
  }

  if (entry?.receiveLastUpdatedAtMs === null && !pendingReceiveProgressEvents.has(event.uploadId)) {
    applyReceiveProgress(event)
    return
  }

  pendingReceiveProgressEvents.set(event.uploadId, event)

  if (event.succeeded || event.state === 'Completed' || event.state === 'Failed') {
    flushReceiveProgressEvents()
    return
  }

  if (receiveProgressFlushTimer) {
    return
  }

  receiveProgressFlushTimer = setTimeout(flushReceiveProgressEvents, receiveProgressUpdateIntervalMs)
}

function flushReceiveProgressEvents() {
  if (receiveProgressFlushTimer) {
    clearTimeout(receiveProgressFlushTimer)
    receiveProgressFlushTimer = null
  }

  const events = Array.from(pendingReceiveProgressEvents.values())
  pendingReceiveProgressEvents.clear()
  for (const event of events) {
    applyReceiveProgress(event)
  }
}

function applyReceiveProgress(event: ReceiveUploadProgressEvent) {
  const entry = uploadEntries.value.find((candidate) => candidate.uploadId === event.uploadId)
  if (!entry) {
    return
  }

  if (entry.state === 'canceled') {
    entry.receiveSpeedBytesPerSecond = null
    return
  }

  const totalBytes = event.totalBytes > 0 ? event.totalBytes : entry.file.size
  const receivedBytes = Math.max(0, Math.min(totalBytes, event.receivedBytes))
  const nowMs = Date.now()
  if (entry.receiveLastUpdatedAtMs !== null) {
    const elapsedSeconds = (nowMs - entry.receiveLastUpdatedAtMs) / 1000
    const byteDelta = receivedBytes - entry.receivedBytes
    if (elapsedSeconds > 0 && byteDelta >= 0) {
      entry.receiveSpeedBytesPerSecond = byteDelta / elapsedSeconds
    }
  }

  entry.receiveTotalBytes = totalBytes
  entry.receivedBytes = receivedBytes
  entry.receiveProgress = totalBytes > 0 ? Math.min(100, (receivedBytes / totalBytes) * 100) : null
  entry.receiveLastUpdatedAtMs = nowMs
  if (typeof event.recommendedChunkSizeBytes === 'number' && Number.isFinite(event.recommendedChunkSizeBytes)) {
    applyReceiveProgressRecommendation(entry, event)
  }

  if (event.state === 'Failed' && entry.state !== 'success' && entry.state !== 'error') {
    entry.state = 'error'
    entry.message = event.error || 'Upload failed.'
    summaryMessage.value = entry.message
  }
}

function applyReceiveProgressRecommendation(entry: UploadEntry | undefined, event: ReceiveUploadProgressEvent) {
  if (!entry || typeof event.recommendedChunkSizeBytes !== 'number' || !Number.isFinite(event.recommendedChunkSizeBytes)) {
    return
  }

  entry.recommendedChunkSizeBytes = normalizeUploadChunkSizeBytes(event.recommendedChunkSizeBytes)
}

onMounted(() => {
  connectUploadEvents()
  window.addEventListener('dragenter', handleWindowDragEnter)
  window.addEventListener('dragover', handleWindowDragOver)
  window.addEventListener('dragleave', handleWindowDragLeave)
  window.addEventListener('drop', handleWindowDrop)
})

onUnmounted(() => {
  uploadEventsSocket?.close()
  uploadEventsSocket = null
  if (receiveProgressFlushTimer) {
    clearTimeout(receiveProgressFlushTimer)
    receiveProgressFlushTimer = null
  }
  pendingReceiveProgressEvents.clear()
  window.removeEventListener('dragenter', handleWindowDragEnter)
  window.removeEventListener('dragover', handleWindowDragOver)
  window.removeEventListener('dragleave', handleWindowDragLeave)
  window.removeEventListener('drop', handleWindowDrop)
})
</script>

<template>
  <section class="stack">
    <div
      class="upload-dropzone"
      :class="{ 'upload-dropzone--active': isDragActive }"
      role="button"
      tabindex="0"
      aria-label="Drop files or folders here, or click to select files."
      @click="openFilePicker"
      @keydown.enter.prevent="openFilePicker"
      @keydown.space.prevent="openFilePicker"
    >
      <input
        ref="filePicker"
        class="upload-picker-input"
        type="file"
        multiple
        tabindex="-1"
        aria-hidden="true"
        @change="handleFilePicker"
        @click.stop
      />
      <span class="upload-dropzone__icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" focusable="false">
          <path d="M12 15V4" />
          <path d="m7 9 5-5 5 5" />
          <path d="M5 15v3a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-3" />
        </svg>
      </span>
    </div>

    <div v-if="uploadEntries.length > 0" class="upload-panel">
      <template v-if="showOverallProgress">
        <div class="progress-summary">
          <strong>Overall progress</strong>
          <span>{{ formatProgress(overallDisplayProgress) }}</span>
        </div>
        <div class="dual-progress overall-progress-track" aria-label="Upload progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" :aria-valuenow="Math.round(overallDisplayProgress)">
          <div
            v-if="hasOverallReceiveProgress"
            class="progress-fill progress-fill--receive"
            :style="{ width: `${overallReceiveProgress}%` }"
          />
          <div
            class="progress-fill progress-fill--client"
            :style="{ width: `${overallProgress}%` }"
          />
        </div>
      </template>
      <p v-if="summaryMessage" class="body-copy">{{ summaryMessage }}</p>

      <div class="upload-list">
        <template v-for="item in uploadListItems" :key="item.key">
          <article v-if="item.type === 'file'" class="upload-item">
            <div class="upload-item__row">
              <div>
                <strong>{{ item.entry.relativePath }}</strong>
                <p>
                  <span>{{ formatBytes(item.entry.file.size) }}</span>
                  <template v-if="shouldShowEntryMessage(item.entry)"> · {{ item.entry.message }}</template>
                </p>
              </div>
              <div class="upload-item__actions">
                <span v-if="item.entry.state === 'success'" class="success-icon" aria-label="Uploaded">
                  <svg viewBox="0 0 24 24" focusable="false" aria-hidden="true">
                    <path d="M20 6 9 17l-5-5" />
                  </svg>
                </span>
                <span v-if="getEntrySpeedLabel(item.entry)" class="speed-label">{{ getEntrySpeedLabel(item.entry) }}</span>
                <span v-if="item.entry.state !== 'success'" class="progress-percent" :class="`progress-percent--${item.entry.state}`">{{ formatProgress(getDisplayProgress(item.entry)) }}</span>
                <button
                  v-if="getEntryActionLabel(item.entry)"
                  class="icon-text-button"
                  type="button"
                  @click="canStopEntry(item.entry) ? stopEntry(item.entry) : restartEntry(item.entry)"
                >
                  {{ getEntryActionLabel(item.entry) }}
                </button>
              </div>
            </div>
            <div v-if="item.entry.state !== 'success'" class="dual-progress compact-track" aria-label="Upload progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" :aria-valuenow="Math.round(getDisplayProgress(item.entry))">
              <div
                v-if="hasReceiveProgress(item.entry)"
                class="progress-fill progress-fill--receive"
                :style="{ width: `${getReceiveProgress(item.entry)}%` }"
              />
              <div
                class="progress-fill progress-fill--client"
                :style="{ width: `${item.entry.progress}%` }"
              />
            </div>
          </article>

          <article v-else class="upload-folder">
            <div class="upload-folder__header">
              <button class="folder-toggle" type="button" @click="toggleFolder(item.label)">
                <span class="folder-toggle__chevron">{{ item.expanded ? 'v' : '>' }}</span>
                <span>
                  <strong>{{ item.label }}</strong>
                  <small>{{ item.entries.length }} item{{ item.entries.length === 1 ? '' : 's' }} · {{ formatBytes(calculateGroupSize(item.entries)) }}</small>
                </span>
              </button>
              <div class="upload-item__actions">
                <span v-if="item.state === 'success'" class="success-icon" aria-label="Uploaded">
                  <svg viewBox="0 0 24 24" focusable="false" aria-hidden="true">
                    <path d="M20 6 9 17l-5-5" />
                  </svg>
                </span>
                <span v-if="getGroupSpeedLabel(item.entries)" class="speed-label">{{ getGroupSpeedLabel(item.entries) }}</span>
                <span v-if="item.state !== 'success'" class="progress-percent" :class="`progress-percent--${item.state}`">{{ formatProgress(getGroupDisplayProgress(item)) }}</span>
                <button
                  v-if="getGroupActionLabel(item.entries)"
                  class="icon-text-button"
                  type="button"
                  @click="handleGroupAction(item)"
                >
                  {{ getGroupActionLabel(item.entries) }}
                </button>
              </div>
            </div>
            <div v-if="item.state !== 'success'" class="dual-progress compact-track" aria-label="Upload progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" :aria-valuenow="Math.round(getGroupDisplayProgress(item))">
              <div
                v-if="hasGroupReceiveProgress(item.entries)"
                class="progress-fill progress-fill--receive"
                :style="{ width: `${item.receiveProgress ?? 0}%` }"
              />
              <div
                class="progress-fill progress-fill--client"
                :style="{ width: `${item.progress}%` }"
              />
            </div>

            <div v-if="item.expanded" class="upload-folder__children">
              <div v-for="entry in item.entries" :key="entry.id" class="upload-child-row">
                <div>
                  <strong>{{ entry.relativePath.slice(item.label.length + 1) }}</strong>
                  <p>
                    <span>{{ formatBytes(entry.file.size) }}</span>
                    <template v-if="shouldShowEntryMessage(entry)"> · {{ entry.message }}</template>
                  </p>
                </div>
                <div class="upload-item__actions">
                  <span v-if="entry.state === 'success'" class="success-icon" aria-label="Uploaded">
                    <svg viewBox="0 0 24 24" focusable="false" aria-hidden="true">
                      <path d="M20 6 9 17l-5-5" />
                    </svg>
                  </span>
                  <span v-if="getEntrySpeedLabel(entry)" class="speed-label">{{ getEntrySpeedLabel(entry) }}</span>
                  <span v-if="entry.state !== 'success'" class="progress-percent" :class="`progress-percent--${entry.state}`">{{ formatProgress(getDisplayProgress(entry)) }}</span>
                  <button
                    v-if="getEntryActionLabel(entry)"
                    class="icon-text-button"
                    type="button"
                    @click="canStopEntry(entry) ? stopEntry(entry) : restartEntry(entry)"
                  >
                    {{ getEntryActionLabel(entry) }}
                  </button>
                </div>
              </div>
            </div>
          </article>
        </template>
      </div>
    </div>
  </section>
</template>
