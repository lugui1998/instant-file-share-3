import type { PublicShareFileModel } from './types'

const uncompressedLengthHeaderName = 'X-IFS-Uncompressed-Length'

export type CompressionRuntimeScope = {
  DecompressionStream?: new (format: CompressionFormat) => GenericTransformStream
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
  const decodedStream = response.body
    .pipeThrough(decompressor)
    .pipeThrough(createDecodedLengthValidator(expectedDecodedBytes))

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
    return { mode: 'compressed-stream' }
  }

  await saveDecodedStreamAsBlob(decodedStream, file.fileName, scope)
  return { mode: 'compressed-blob' }
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

function createDecodedLengthValidator(expectedBytes: number): TransformStream<Uint8Array, Uint8Array> {
  let decodedBytes = 0

  return new TransformStream<Uint8Array, Uint8Array>({
    transform(chunk, controller) {
      decodedBytes += chunk.byteLength

      if (decodedBytes > expectedBytes) {
        throw new Error(`Compressed download decoded more bytes than expected (${decodedBytes} > ${expectedBytes}).`)
      }

      controller.enqueue(chunk)
    },
    flush() {
      if (decodedBytes !== expectedBytes) {
        throw new Error(`Compressed download decoded ${decodedBytes} bytes, expected ${expectedBytes} bytes.`)
      }
    },
  })
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
