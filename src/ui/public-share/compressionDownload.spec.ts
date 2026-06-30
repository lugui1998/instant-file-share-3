import { describe, expect, it, vi } from 'vitest'
import {
  buildManagedGzipUrl,
  chooseManagedCompressionMethod,
  createManagedCompressionProbeState,
  createRawCompressionSample,
  downloadFileWithCompressionFallback,
  estimateBlobFallbackMemoryCost,
  formatTransferBitsPerSecond,
  readManagedGzipResponseAsBuffer,
  recordManagedCompressionSample,
  supportsGzipDecompression,
  supportsStreamingFileSave,
} from './compressionDownload'
import type { PublicShareFileModel } from './types'

class IdentityDecompressionStream {
  readonly readable: ReadableStream<Uint8Array>
  readonly writable: WritableStream<Uint8Array>

  constructor(_format: CompressionFormat) {
    const stream = new TransformStream<Uint8Array, Uint8Array>()
    this.readable = stream.readable
    this.writable = stream.writable
  }
}

class FailingDecompressionStream {
  readonly readable: ReadableStream<Uint8Array>
  readonly writable: WritableStream<Uint8Array>

  constructor(_format: CompressionFormat) {
    const stream = new TransformStream<Uint8Array, Uint8Array>({
      transform() {
        throw new Error('gzip stream is corrupt')
      },
    })
    this.readable = stream.readable
    this.writable = stream.writable
  }
}

describe('compressionDownload', () => {
  it('detects browser decompression and streaming save support', () => {
    expect(supportsGzipDecompression({ DecompressionStream: IdentityDecompressionStream })).toBe(true)
    expect(supportsGzipDecompression({})).toBe(false)
    expect(supportsStreamingFileSave({ showSaveFilePicker: vi.fn() })).toBe(true)
    expect(supportsStreamingFileSave({})).toBe(false)
  })

  it('falls back to the raw download when decompression is unsupported', async () => {
    const assign = vi.fn()

    const result = await downloadFileWithCompressionFallback(createFile(), {
      location: { assign },
    })

    expect(result.mode).toBe('raw-fallback')
    expect(assign).toHaveBeenCalledWith('https://share.example.test/s/token/report.csv')
  })

  it('streams decoded bytes to the File System Access API when available', async () => {
    const chunks: number[] = []
    const writable = new WritableStream<Uint8Array>({
      write(chunk) {
        chunks.push(...chunk)
      },
    })
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(Uint8Array.from([65, 66]), {
        headers: { 'X-IFS-Uncompressed-Length': '2' },
      }),
    )

    const result = await downloadFileWithCompressionFallback(createFile(), {
      DecompressionStream: IdentityDecompressionStream,
      fetch: fetchMock,
      showSaveFilePicker: vi.fn().mockResolvedValue({
        createWritable: vi.fn().mockResolvedValue(writable),
      }),
      location: { assign: vi.fn() },
    })

    expect(result.mode).toBe('compressed-stream')
    expect(fetchMock).toHaveBeenCalledWith('https://share.example.test/s/token/report.csv?compression=gzip')
    expect(chunks).toEqual([65, 66])
    expect(result.compressionSample).toMatchObject({
      method: 'gzip',
      logicalBytes: 2,
      wireBytes: 2,
      ok: true,
    })
  })

  it('rejects oversized decoded output without falling back to the raw download', async () => {
    const assign = vi.fn()
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(Uint8Array.from([65, 66]), {
        headers: { 'X-IFS-Uncompressed-Length': '1' },
      }),
    )

    await expect(
      downloadFileWithCompressionFallback(createFile(), {
        DecompressionStream: IdentityDecompressionStream,
        fetch: fetchMock,
        location: { assign },
      }),
    ).rejects.toThrow('decoded more bytes than expected')

    expect(assign).not.toHaveBeenCalled()
  })

  it('rejects decoded length mismatches without falling back to the raw download', async () => {
    const assign = vi.fn()
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(Uint8Array.from([65, 66]), {
        headers: { 'X-IFS-Uncompressed-Length': '3' },
      }),
    )

    await expect(
      downloadFileWithCompressionFallback(createFile(), {
        DecompressionStream: IdentityDecompressionStream,
        fetch: fetchMock,
        location: { assign },
      }),
    ).rejects.toThrow('decoded 2 bytes, expected 3 bytes')

    expect(assign).not.toHaveBeenCalled()
  })

  it('does not fall back to the raw download when browser decompression fails', async () => {
    const assign = vi.fn()
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(Uint8Array.from([31, 139]), {
        headers: { 'X-IFS-Uncompressed-Length': '4096' },
      }),
    )

    await expect(
      downloadFileWithCompressionFallback(createFile(), {
        DecompressionStream: FailingDecompressionStream,
        fetch: fetchMock,
        location: { assign },
      }),
    ).rejects.toThrow('gzip stream is corrupt')

    expect(assign).not.toHaveBeenCalled()
  })

  it('records Blob fallback memory as decoded bytes buffered', () => {
    expect(estimateBlobFallbackMemoryCost(4096)).toEqual({
      decodedBytesBuffered: 4096,
      minimumTransientBytes: 4096,
    })
  })

  it('formats transfer speeds in bits per second', () => {
    expect(formatTransferBitsPerSecond(16)).toBe('128 b/s')
    expect(formatTransferBitsPerSecond(2048)).toBe('16 Kb/s')
    expect(formatTransferBitsPerSecond(1024 * 1024)).toBe('8 Mb/s')
  })

  it('adds managed gzip to signed raw chunk URLs without dropping plan query parameters', () => {
    expect(buildManagedGzipUrl('https://share.example.test/s/file-token?download=raw&ifsPlanHash=abc')).toBe(
      'https://share.example.test/s/file-token?download=raw&ifsPlanHash=abc&compression=gzip',
    )
  })

  it('chooses gzip first, then raw as a baseline, then keeps gzip while it wins', () => {
    let state = createManagedCompressionProbeState()

    expect(chooseManagedCompressionMethod({ state, canUseGzip: true })).toEqual({
      method: 'gzip',
      reason: 'probing managed gzip',
    })

    state = recordManagedCompressionSample(state, {
      method: 'gzip',
      logicalBytes: 4 * 1024 * 1024,
      wireBytes: 1024 * 1024,
      transferDurationMs: 1000,
      decompressionDurationMs: 50,
      ok: true,
    })

    expect(chooseManagedCompressionMethod({ state, canUseGzip: true })).toEqual({
      method: 'raw',
      reason: 'probing raw baseline',
    })

    state = recordManagedCompressionSample(state, createRawCompressionSample(4 * 1024 * 1024, 2500))

    expect(state.gzipDisabled).toBe(false)
    expect(chooseManagedCompressionMethod({ state, canUseGzip: true })).toEqual({
      method: 'gzip',
      reason: 'gzip probe is currently winning',
    })
  })

  it('disables gzip when the compressed transfer is effectively raw-sized', () => {
    const state = recordManagedCompressionSample(createManagedCompressionProbeState(), {
      method: 'gzip',
      logicalBytes: 1024 * 1024,
      wireBytes: 1020 * 1024,
      transferDurationMs: 100,
      decompressionDurationMs: 10,
      ok: true,
    })

    expect(state.gzipDisabled).toBe(true)
    expect(chooseManagedCompressionMethod({ state, canUseGzip: true }).method).toBe('raw')
    expect(state.disabledReason).toContain('using raw chunks')
  })

  it('disables gzip when effective logical throughput loses to the raw baseline', () => {
    let state = createManagedCompressionProbeState()
    state = recordManagedCompressionSample(state, createRawCompressionSample(4 * 1024 * 1024, 400))
    state = recordManagedCompressionSample(state, {
      method: 'gzip',
      logicalBytes: 4 * 1024 * 1024,
      wireBytes: 1024 * 1024,
      transferDurationMs: 300,
      decompressionDurationMs: 1000,
      ok: true,
    })

    expect(state.gzipDisabled).toBe(true)
    expect(state.disabledReason).toBe('gzip effective speed was slower than raw; using raw chunks')
  })

  it('decodes managed gzip responses and reports wire bytes and timings', async () => {
    let timestamp = 0
    const result = await readManagedGzipResponseAsBuffer(
      new Response(Uint8Array.from([65, 66, 67])),
      3,
      {
        DecompressionStream: IdentityDecompressionStream,
        performance: {
          now: () => {
            timestamp += 10
            return timestamp
          },
        },
      },
    )

    expect(Array.from(new Uint8Array(result.buffer))).toEqual([65, 66, 67])
    expect(result.sample).toMatchObject({
      method: 'gzip',
      logicalBytes: 3,
      wireBytes: 3,
      transferDurationMs: 10,
      decompressionDurationMs: 10,
      ratio: 1,
      ok: true,
    })
  })
})

function createFile(): PublicShareFileModel {
  return {
    fileName: 'report.csv',
    displaySize: '4.0 KB',
    sizeBytes: 4096,
    preferInline: false,
    canUseBrowserCompression: true,
    rawDownloadUrl: 'https://share.example.test/s/token/report.csv',
    compressedDownloadUrl: 'https://share.example.test/s/token/report.csv?compression=gzip',
    actionVerb: 'Download',
    actionLabel: 'Open this link to download report.csv.',
  }
}
