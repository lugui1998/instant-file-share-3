import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  blobManagedDownloadMaxBytes,
  largeFileStreamingRequiredMessage,
} from '../../downloadState'
import type {
  BrowserManagedDownloadPlan,
  PublicShareFileModel,
  PublicSharePageModel,
} from '../../types'
import FileSharePage from './FileSharePage.vue'

class IdentityDecompressionStream {
  readonly readable: ReadableStream<Uint8Array>
  readonly writable: WritableStream<Uint8Array>

  constructor(_format: CompressionFormat) {
    const stream = new TransformStream<Uint8Array, Uint8Array>()
    this.readable = stream.readable
    this.writable = stream.writable
  }
}

describe('FileSharePage', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('disables large browser-managed downloads with a direct-download fallback when streaming is unavailable', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => createPlan({
        fileSizeBytes: blobManagedDownloadMaxBytes + 1,
        chunks: [],
      }),
    })
    vi.stubGlobal('fetch', fetchMock)
    vi.stubGlobal('showSaveFilePicker', undefined)

    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile(),
      },
    })

    await vi.waitFor(() => {
      expect(wrapper.text()).toContain(largeFileStreamingRequiredMessage)
    })
    expect(wrapper.get('button.button--primary').attributes('disabled')).toBeDefined()
    expect(wrapper.get('a.button--ghost').attributes('href')).toBe('https://public.example/s/file-token?download=raw')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('shows a stale live-file message when a chunk hash fails', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        json: async () => createPlan({
          fileSizeBytes: 3,
          chunks: [{
            index: 0,
            start: 0,
            end: 2,
            sizeBytes: 3,
            sha256: '0000000000000000000000000000000000000000000000000000000000000000',
          }],
        }),
      })
      .mockResolvedValue({
        ok: true,
        status: 206,
        arrayBuffer: async () => new TextEncoder().encode('new').buffer,
      }))

    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile({
          displaySize: '3 B',
          sizeBytes: 3,
          managedDownload: {
            ...createSmallManagedDownload(),
            fileSizeBytes: 3,
          },
        }),
      },
    })

    await vi.waitFor(() => {
      expect(wrapper.get('button.button--primary').attributes('disabled')).toBeUndefined()
    })
    await wrapper.get('button.button--primary').trigger('click')

    await vi.waitFor(() => {
      expect(wrapper.text()).toContain('The shared file may have changed after the download plan was created.')
    })
  })

  it('reports compression probe diagnostics and disables gzip when the chunk does not shrink', async () => {
    const bytes = new TextEncoder().encode('abc')
    vi.stubGlobal('DecompressionStream', IdentityDecompressionStream)
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = vi.fn(() => 'blob:download')
      static revokeObjectURL = vi.fn()
    })
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        json: async () => createPlan({
          fileSizeBytes: bytes.byteLength,
          chunks: [{
            index: 0,
            start: 0,
            end: bytes.byteLength - 1,
            sizeBytes: bytes.byteLength,
            sha256: await sha256Hex(bytes.buffer),
          }],
        }),
      })
      .mockResolvedValueOnce(new Response(bytes)))

    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile({
          displaySize: '3 B',
          sizeBytes: bytes.byteLength,
          canUseBrowserCompression: true,
          compressedDownloadUrl: 'https://public.example/s/file-token?download=raw&compression=gzip',
          managedDownload: {
            ...createFile().managedDownload!,
            fileSizeBytes: bytes.byteLength,
            transferDiagnosticsEnabled: true,
          },
        }),
      },
    })

    await vi.waitFor(() => {
      expect(wrapper.get('button.button--primary').attributes('disabled')).toBeUndefined()
    })
    await wrapper.get('button.button--primary').trigger('click')

    await vi.waitFor(() => {
      expect(wrapper.text()).toContain('Complete')
    })

    expect(fetch).toHaveBeenNthCalledWith(2, expect.stringContaining('&compression=gzip'), expect.objectContaining({
      headers: { Range: 'bytes=0-2' },
      cache: 'no-store',
    }))
    expect(wrapper.text()).toContain('3 B wire / 3 B logical (100%)')
    expect(wrapper.text()).toContain('raw - gzip saved less than')
  })

  it('automatically retries transient managed download plan failures', async () => {
    vi.useFakeTimers()
    const bytes = new TextEncoder().encode('abc')
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = vi.fn(() => 'blob:download')
      static revokeObjectURL = vi.fn()
    })
    vi.stubGlobal('fetch', vi.fn()
      .mockRejectedValueOnce(new TypeError('network lost'))
      .mockResolvedValueOnce({
        ok: true,
        json: async () => createPlan({
          fileSizeBytes: bytes.byteLength,
          chunks: [{
            index: 0,
            start: 0,
            end: bytes.byteLength - 1,
            sizeBytes: bytes.byteLength,
            sha256: await sha256Hex(bytes.buffer),
          }],
        }),
      })
      .mockResolvedValueOnce({
        ok: true,
        status: 206,
        arrayBuffer: async () => bytes.buffer,
      }))

    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile({
          displaySize: '3 B',
          sizeBytes: bytes.byteLength,
          managedDownload: {
            ...createSmallManagedDownload(),
            fileSizeBytes: bytes.byteLength,
          },
        }),
      },
    })

    await vi.waitFor(() => {
      expect(wrapper.get('button.button--primary').attributes('disabled')).toBeUndefined()
    })
    await wrapper.get('button.button--primary').trigger('click')
    await vi.dynamicImportSettled()
    await vi.advanceTimersByTimeAsync(1000)
    await vi.dynamicImportSettled()

    await vi.waitFor(() => {
      expect(wrapper.text()).toContain('Complete')
    })
    expect(fetch).toHaveBeenCalledTimes(3)
  })

  it('automatically retries transient managed download chunk failures', async () => {
    vi.useFakeTimers()
    const bytes = new TextEncoder().encode('abc')
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = vi.fn(() => 'blob:download')
      static revokeObjectURL = vi.fn()
    })
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        json: async () => createPlan({
          fileSizeBytes: bytes.byteLength,
          chunks: [{
            index: 0,
            start: 0,
            end: bytes.byteLength - 1,
            sizeBytes: bytes.byteLength,
            sha256: await sha256Hex(bytes.buffer),
          }],
        }),
      })
      .mockRejectedValueOnce(new TypeError('network lost'))
      .mockResolvedValueOnce({
        ok: true,
        status: 206,
        arrayBuffer: async () => bytes.buffer,
      }))

    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile({
          displaySize: '3 B',
          sizeBytes: bytes.byteLength,
          managedDownload: {
            ...createSmallManagedDownload(),
            fileSizeBytes: bytes.byteLength,
          },
        }),
      },
    })

    await vi.waitFor(() => {
      expect(wrapper.get('button.button--primary').attributes('disabled')).toBeUndefined()
    })
    await wrapper.get('button.button--primary').trigger('click')
    await vi.dynamicImportSettled()
    await vi.advanceTimersByTimeAsync(1000)
    await vi.dynamicImportSettled()

    await vi.waitFor(() => {
      expect(wrapper.text()).toContain('Complete')
    })
    expect(fetch).toHaveBeenCalledTimes(3)
  })

  it('hides transfer diagnostics unless the host enables them', async () => {
    const hiddenWrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile({
          displaySize: '3 B',
          sizeBytes: 3,
          managedDownload: createSmallManagedDownload(),
        }),
      },
    })

    expect(hiddenWrapper.find('.transfer-diagnostics').exists()).toBe(false)

    const visibleWrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile({
          displaySize: '3 B',
          sizeBytes: 3,
          managedDownload: {
            ...createSmallManagedDownload(),
            transferDiagnosticsEnabled: true,
          },
        }),
      },
    })

    expect(visibleWrapper.find('.transfer-diagnostics').exists()).toBe(true)
    expect(visibleWrapper.text()).toContain('Concurrency')
    expect(visibleWrapper.text()).toContain('Compression')
  })
})

function createPage(): PublicSharePageModel {
  return {
    kind: 'file',
    title: 'large.bin',
    description: 'Download large.bin.',
    canonicalUrl: 'https://public.example/s/file-token',
    siteName: 'Instant File Share',
    repositoryUrl: 'https://github.com/lugui1998/instant-file-share-3',
  }
}

function createFile(overrides: Partial<PublicShareFileModel> = {}): PublicShareFileModel {
  const managedDownload = overrides.managedDownload === undefined
    ? createLargeManagedDownload()
    : overrides.managedDownload

  return {
    fileName: 'large.bin',
    displaySize: '513 MB',
    sizeBytes: blobManagedDownloadMaxBytes + 1,
    preferInline: false,
    canUseBrowserCompression: false,
    rawDownloadUrl: 'https://public.example/s/file-token?download=raw',
    compressedDownloadUrl: null,
    actionVerb: 'Download',
    actionLabel: 'Open this link to download large.bin.',
    managedDownload,
    ...overrides,
  }
}

function createLargeManagedDownload(): NonNullable<PublicShareFileModel['managedDownload']> {
  return {
    ...createSmallManagedDownload(),
    fileSizeBytes: blobManagedDownloadMaxBytes + 1,
  }
}

function createSmallManagedDownload(): NonNullable<PublicShareFileModel['managedDownload']> {
  return {
    manifestUrl: 'https://public.example/s/file-token?ifs=download-plan',
    rawDownloadUrl: 'https://public.example/s/file-token?download=raw',
    fileSizeBytes: 3,
    defaultChunkSizeBytes: 4 * 1024 * 1024,
    maxRetriesPerChunk: 3,
    maxMemoryBytes: blobManagedDownloadMaxBytes,
    maxParallelChunks: 4,
    compressionMode: 'Auto' as const,
    transferDiagnosticsEnabled: false,
    saveLimitationNote: 'Browser-managed download note.',
  }
}

function createPlan(overrides: Partial<BrowserManagedDownloadPlan>): BrowserManagedDownloadPlan {
  return {
    fileName: 'large.bin',
    fileSizeBytes: 3,
    contentType: 'application/octet-stream',
    rawDownloadUrl: 'https://public.example/s/file-token?download=raw',
    rangeUnit: 'bytes',
    integrityAlgorithm: 'sha-256',
    lastModifiedUtc: '2026-06-30T00:00:00.0000000Z',
    lastModifiedUtcTicks: 638868960000000000,
    planHash: 'plan-hash',
    planSignature: 'plan-signature',
    chunkSizeBytes: 4 * 1024 * 1024,
    maxRetriesPerChunk: 0,
    chunks: [],
    saveLimitationNote: 'Browser-managed download note.',
    ...overrides,
  }
}

async function sha256Hex(buffer: ArrayBuffer) {
  const hash = await crypto.subtle.digest('SHA-256', buffer)
  return Array.from(new Uint8Array(hash))
    .map((byte) => byte.toString(16).padStart(2, '0'))
    .join('')
}
