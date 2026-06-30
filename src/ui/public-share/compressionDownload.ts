import type {
  ManagedCompressionDecision,
  ManagedCompressionProbeState,
  ManagedCompressionSample,
  ManagedCompressionSampleSummary,
  PublicShareFileModel,
} from './types'

const uncompressedLengthHeaderName = 'X-IFS-Uncompressed-Length'
const compressionQueryName = 'compression'
const gzipCompressionQueryValue = 'gzip'
const gzipLossRatioThreshold = 0.98
const gzipSpeedLossTolerance = 0.95

export type CompressionRuntimeScope = {
  DecompressionStream?: new (format: CompressionFormat) => GenericTransformStream
  performance?: Pick<Performance, 'now'>
  showSaveFilePicker?: (options?: {
    suggestedName?: string
    types?: Array<{
      description: string
      accept: Record<string, string[]>
    }>
  }) => Promise<{
    createWritable: () => Promise<WritableStream<Uint8Array>>
  }>
  fetch?: typeof fetch
  URL?: Pick<typeof URL, 'createObjectURL' | 'revokeObjectURL'>
  document?: Pick<Document, 'createElement' | 'body'>
  location?: Pick<Location, 'assign'>
}

export type BlobMemoryEstimate = {
  decodedBytesBuffered: number
  minimumTransientBytes: number
}

export type DownloadResult = {
  mode: 'compressed-stream' | 'compressed-blob' | 'raw-fallback'
  reason?: string
  compressionSample?: ManagedCompressionSampleSummary
}

export function supportsGzipDecompression(scope: CompressionRuntimeScope = globalThis): boolean {
  return typeof scope.DecompressionStream === 'function'
}

export function supportsStreamingFileSave(scope: CompressionRuntimeScope = globalThis): boolean {
  return typeof scope.showSaveFilePicker === 'function'
}

export function estimateBlobFallbackMemoryCost(decodedSizeBytes: number): BlobMemoryEstimate {
  const safeDecodedSize = Number.isFinite(decodedSizeBytes) && decodedSizeBytes > 0 ? decodedSizeBytes : 0
  return {
    decodedBytesBuffered: safeDecodedSize,
    minimumTransientBytes: safeDecodedSize,
  }
}

export function formatTransferBytes(bytes: number): string {
  const safeBytes = Number.isFinite(bytes) && bytes > 0 ? bytes : 0
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let size = safeBytes
  let unitIndex = 0

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024
    unitIndex += 1
  }

  return unitIndex === 0 ? `${size.toFixed(0)} ${units[unitIndex]}` : `${size.toFixed(1)} ${units[unitIndex]}`
}

export function formatTransferBitsPerSecond(bytesPerSecond: number): string {
  const safeBits = Number.isFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond * 8 : 0
  const units = ['b/s', 'Kb/s', 'Mb/s', 'Gb/s', 'Tb/s']
  let size = safeBits
  let unitIndex = 0

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024
    unitIndex += 1
  }

  return unitIndex === 0 ? `${size.toFixed(0)} ${units[unitIndex]}` : `${size.toFixed(1).replace(/\.0$/, '')} ${units[unitIndex]}`
}

export function createManagedCompressionProbeState(): ManagedCompressionProbeState {
  return {
    gzipDisabled: false,
    decisionReason: 'waiting for transfer samples',
    rawSampleCount: 0,
    gzipSampleCount: 0,
    rawLogicalBytes: 0,
    rawWireBytes: 0,
    rawTransferDurationMs: 0,
    gzipLogicalBytes: 0,
    gzipWireBytes: 0,
    gzipTransferDurationMs: 0,
    gzipDecompressionDurationMs: 0,
  }
}

export function chooseManagedCompressionMethod(options: {
  state: ManagedCompressionProbeState
  canUseGzip: boolean
}): ManagedCompressionDecision {
  if (!options.canUseGzip) {
    return { method: 'raw', reason: 'gzip is unavailable for this transfer' }
  }

  if (options.state.gzipDisabled) {
    return { method: 'raw', reason: options.state.disabledReason ?? 'gzip lost the transfer probe' }
  }

  if (options.state.gzipSampleCount === 0) {
    return { method: 'gzip', reason: 'probing managed gzip' }
  }

  if (options.state.rawSampleCount === 0) {
    return { method: 'raw', reason: 'probing raw baseline' }
  }

  return { method: 'gzip', reason: 'gzip probe is currently winning' }
}

export function recordManagedCompressionSample(
  state: ManagedCompressionProbeState,
  sample: ManagedCompressionSample,
): ManagedCompressionProbeState {
  const summary = summarizeManagedCompressionSample(sample)
  const nextState: ManagedCompressionProbeState = { ...state }

  if (summary.method === 'raw') {
    nextState.rawSampleCount += 1
    nextState.rawLogicalBytes += summary.logicalBytes
    nextState.rawWireBytes += summary.wireBytes
    nextState.rawTransferDurationMs += summary.transferDurationMs
    nextState.lastRawSample = summary
  } else {
    nextState.gzipSampleCount += 1
    nextState.gzipLogicalBytes += summary.logicalBytes
    nextState.gzipWireBytes += summary.wireBytes
    nextState.gzipTransferDurationMs += summary.transferDurationMs
    nextState.gzipDecompressionDurationMs += summary.decompressionDurationMs
    nextState.lastGzipSample = summary
  }

  const lossReason = resolveGzipLossReason(nextState)
  if (lossReason) {
    nextState.gzipDisabled = true
    nextState.disabledReason = lossReason
    nextState.decisionReason = lossReason
    return nextState
  }

  nextState.decisionReason = chooseManagedCompressionMethod({ state: nextState, canUseGzip: true }).reason
  return nextState
}

export function summarizeManagedCompressionSample(sample: ManagedCompressionSample): ManagedCompressionSampleSummary {
  const logicalBytes = sanitizeByteCount(sample.logicalBytes)
  const wireBytes = sanitizeByteCount(sample.wireBytes)
  const transferDurationMs = sanitizeDuration(sample.transferDurationMs)
  const decompressionDurationMs = sanitizeDuration(sample.decompressionDurationMs)
  const totalDurationMs = Math.max(transferDurationMs + decompressionDurationMs, 1)

  return {
    ...sample,
    logicalBytes,
    wireBytes,
    transferDurationMs,
    decompressionDurationMs,
    ratio: logicalBytes > 0 ? wireBytes / logicalBytes : 1,
    wireBytesPerSecond: (wireBytes / Math.max(transferDurationMs, 1)) * 1000,
    effectiveBytesPerSecond: (logicalBytes / totalDurationMs) * 1000,
  }
}

export function buildManagedGzipUrl(rawDownloadUrl: string): string {
  try {
    const isAbsoluteUrl = /^[a-z][a-z\d+\-.]*:/i.test(rawDownloadUrl)
    const parsedUrl = new URL(rawDownloadUrl, isAbsoluteUrl ? undefined : 'http://instant-file-share.local')
    parsedUrl.searchParams.set(compressionQueryName, gzipCompressionQueryValue)

    return isAbsoluteUrl
      ? parsedUrl.toString()
      : `${parsedUrl.pathname}${parsedUrl.search}${parsedUrl.hash}`
  } catch {
    const separator = rawDownloadUrl.includes('?') ? '&' : '?'
    return `${rawDownloadUrl}${separator}${encodeURIComponent(compressionQueryName)}=${encodeURIComponent(gzipCompressionQueryValue)}`
  }
}

export async function readManagedGzipResponseAsBuffer(
  response: Response,
  expectedDecodedBytes: number,
  scope: CompressionRuntimeScope = globalThis,
): Promise<{
  buffer: ArrayBuffer
  sample: ManagedCompressionSampleSummary
}> {
  if (!response.ok || !scope.DecompressionStream) {
    throw new Error(`Compressed chunk returned HTTP ${response.status}.`)
  }

  const transferStart = now(scope)
  const compressedBuffer = await response.arrayBuffer()
  const transferDurationMs = now(scope) - transferStart
  const decompressionStart = now(scope)
  const decompressor = new scope.DecompressionStream('gzip')
  const compressedStream = new Response(compressedBuffer).body
  if (!compressedStream) {
    throw new Error('Compressed chunk did not expose a readable body.')
  }

  const decodedBuffer = await new Response(compressedStream.pipeThrough(decompressor)).arrayBuffer()
  const decompressionDurationMs = now(scope) - decompressionStart

  if (decodedBuffer.byteLength !== expectedDecodedBytes) {
    throw new Error(`Compressed chunk decoded ${decodedBuffer.byteLength} bytes, expected ${expectedDecodedBytes} bytes.`)
  }

  return {
    buffer: decodedBuffer,
    sample: summarizeManagedCompressionSample({
      method: 'gzip',
      logicalBytes: decodedBuffer.byteLength,
      wireBytes: compressedBuffer.byteLength,
      transferDurationMs,
      decompressionDurationMs,
      ok: true,
    }),
  }
}

export function createRawCompressionSample(
  logicalBytes: number,
  transferDurationMs: number,
  ok = true,
): ManagedCompressionSampleSummary {
  return summarizeManagedCompressionSample({
    method: 'raw',
    logicalBytes,
    wireBytes: logicalBytes,
    transferDurationMs,
    decompressionDurationMs: 0,
    ok,
  })
}

export async function downloadFileWithCompressionFallback(
  file: PublicShareFileModel,
  scope: CompressionRuntimeScope = globalThis,
): Promise<DownloadResult> {
  if (!file.compressedDownloadUrl || !supportsGzipDecompression(scope)) {
    startRawDownload(file.rawDownloadUrl, scope)
    return { mode: 'raw-fallback', reason: 'gzip decompression is not available' }
  }

  const fetchImpl = scope.fetch ?? fetch
  let response: Response
  try {
    response = await fetchImpl(file.compressedDownloadUrl)
  } catch (error) {
    startRawDownload(file.rawDownloadUrl, scope)
    return {
      mode: 'raw-fallback',
      reason: error instanceof Error ? error.message : 'compressed response was unavailable',
    }
  }

  if (!response.ok || !response.body || !scope.DecompressionStream) {
    startRawDownload(file.rawDownloadUrl, scope)
    return { mode: 'raw-fallback', reason: 'compressed response was unavailable' }
  }

  const expectedDecodedBytes = getExpectedDecodedLength(response.headers, file)
  const decompressor = new scope.DecompressionStream('gzip')
  let compressedWireBytes = 0
  let decodedBytes = 0
  const transferStart = now(scope)
  let transferDurationMs = 0
  let decompressionDurationMs = 0
  const decodedStream = response.body
    .pipeThrough(createByteCounter((bytes) => {
      compressedWireBytes += bytes
    }, () => {
      transferDurationMs = now(scope) - transferStart
    }))
    .pipeThrough(decompressor)
    .pipeThrough(createDecodedLengthValidator(expectedDecodedBytes, (bytes) => {
      decodedBytes += bytes
    }, () => {
      decompressionDurationMs = Math.max(0, now(scope) - transferStart - transferDurationMs)
    }))

  if (supportsStreamingFileSave(scope) && scope.showSaveFilePicker) {
    const handle = await scope.showSaveFilePicker({
      suggestedName: file.fileName,
      types: [
        {
          description: 'Downloaded file',
          accept: { 'application/octet-stream': ['.*'] },
        },
      ],
    })
    const writable = await handle.createWritable()
    await decodedStream.pipeTo(writable)
    return {
      mode: 'compressed-stream',
      compressionSample: summarizeManagedCompressionSample({
        method: 'gzip',
        logicalBytes: decodedBytes,
        wireBytes: compressedWireBytes,
        transferDurationMs,
        decompressionDurationMs,
        ok: true,
      }),
    }
  }

  await saveDecodedStreamAsBlob(decodedStream, file.fileName, scope)
  return {
    mode: 'compressed-blob',
    compressionSample: summarizeManagedCompressionSample({
      method: 'gzip',
      logicalBytes: decodedBytes,
      wireBytes: compressedWireBytes,
      transferDurationMs,
      decompressionDurationMs,
      ok: true,
    }),
  }
}

function getExpectedDecodedLength(headers: Headers, file: PublicShareFileModel): number {
  const advertisedLength = headers.get(uncompressedLengthHeaderName)
  if (advertisedLength !== null) {
    return parseExpectedDecodedLength(advertisedLength, uncompressedLengthHeaderName)
  }

  return parseExpectedDecodedLength(String(file.sizeBytes), 'file size')
}

function parseExpectedDecodedLength(value: string, source: string): number {
  const expectedLength = Number(value)
  if (!Number.isSafeInteger(expectedLength) || expectedLength < 0) {
    throw new Error(`Invalid ${source}: ${value}`)
  }

  return expectedLength
}

function createDecodedLengthValidator(
  expectedBytes: number,
  onChunk?: (bytes: number) => void,
  onFlush?: () => void,
): TransformStream<Uint8Array, Uint8Array> {
  let decodedBytes = 0

  return new TransformStream<Uint8Array, Uint8Array>({
    transform(chunk, controller) {
      decodedBytes += chunk.byteLength
      onChunk?.(chunk.byteLength)

      if (decodedBytes > expectedBytes) {
        throw new Error(`Compressed download decoded more bytes than expected (${decodedBytes} > ${expectedBytes}).`)
      }

      controller.enqueue(chunk)
    },
    flush() {
      if (decodedBytes !== expectedBytes) {
        throw new Error(`Compressed download decoded ${decodedBytes} bytes, expected ${expectedBytes} bytes.`)
      }

      onFlush?.()
    },
  })
}

function createByteCounter(
  onChunk: (bytes: number) => void,
  onFlush?: () => void,
): TransformStream<Uint8Array, Uint8Array> {
  return new TransformStream<Uint8Array, Uint8Array>({
    transform(chunk, controller) {
      onChunk(chunk.byteLength)
      controller.enqueue(chunk)
    },
    flush() {
      onFlush?.()
    },
  })
}

function resolveGzipLossReason(state: ManagedCompressionProbeState): string | undefined {
  if (state.lastGzipSample && !state.lastGzipSample.ok) {
    return 'gzip failed; using raw chunks'
  }

  const gzipSample = state.lastGzipSample
  if (!gzipSample) {
    return undefined
  }

  if (gzipSample.ratio >= gzipLossRatioThreshold) {
    return `gzip saved less than ${Math.round((1 - gzipLossRatioThreshold) * 100)}%; using raw chunks`
  }

  const rawSample = state.lastRawSample
  if (rawSample && gzipSample.effectiveBytesPerSecond < rawSample.effectiveBytesPerSecond * gzipSpeedLossTolerance) {
    return 'gzip effective speed was slower than raw; using raw chunks'
  }

  return undefined
}

function sanitizeByteCount(value: number): number {
  return Number.isFinite(value) && value > 0 ? value : 0
}

function sanitizeDuration(value: number): number {
  return Number.isFinite(value) && value > 0 ? value : 0
}

function now(scope: CompressionRuntimeScope): number {
  return scope.performance?.now() ?? Date.now()
}

async function saveDecodedStreamAsBlob(
  decodedStream: ReadableStream<Uint8Array>,
  fileName: string,
  scope: CompressionRuntimeScope,
): Promise<void> {
  const blob = await new Response(decodedStream).blob()
  const urlApi = scope.URL ?? URL
  const objectUrl = urlApi.createObjectURL(blob)
  const documentRef = scope.document ?? document
  const link = documentRef.createElement('a')
  link.href = objectUrl
  link.download = fileName
  link.rel = 'noreferrer'
  link.style.display = 'none'
  documentRef.body.appendChild(link)
  link.click()
  link.remove()
  urlApi.revokeObjectURL(objectUrl)
}

function startRawDownload(url: string, scope: CompressionRuntimeScope): void {
  const locationRef = scope.location ?? window.location
  locationRef.assign(url)
}
