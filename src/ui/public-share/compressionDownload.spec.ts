import { describe, expect, it, vi } from 'vitest'
import {
  downloadFileWithCompressionFallback,
  estimateBlobFallbackMemoryCost,
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
