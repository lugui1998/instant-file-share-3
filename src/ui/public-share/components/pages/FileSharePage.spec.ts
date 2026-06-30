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

describe('FileSharePage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('fails large browser-managed downloads with a direct-download fallback when streaming is unavailable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: async () => createPlan({
        fileSizeBytes: blobManagedDownloadMaxBytes + 1,
        chunks: [],
      }),
    }))
    vi.stubGlobal('showSaveFilePicker', undefined)

    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile(),
      },
    })

    await wrapper.get('button.button--primary').trigger('click')

    await vi.waitFor(() => {
      expect(wrapper.text()).toContain(largeFileStreamingRequiredMessage)
    })
    expect(wrapper.get('a.button--ghost').attributes('href')).toBe('https://public.example/s/file-token?download=raw')
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
        file: createFile(),
      },
    })

    await wrapper.get('button.button--primary').trigger('click')

    await vi.waitFor(() => {
      expect(wrapper.text()).toContain('The shared file may have changed after the download plan was created.')
    })
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

function createFile(): PublicShareFileModel {
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
    managedDownload: {
      manifestUrl: 'https://public.example/s/file-token?ifs=download-plan',
      rawDownloadUrl: 'https://public.example/s/file-token?download=raw',
      fileSizeBytes: blobManagedDownloadMaxBytes + 1,
      defaultChunkSizeBytes: 4 * 1024 * 1024,
      maxRetriesPerChunk: 3,
      saveLimitationNote: 'Browser-managed download note.',
    },
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
    chunkSizeBytes: 4 * 1024 * 1024,
    maxRetriesPerChunk: 0,
    chunks: [],
    saveLimitationNote: 'Browser-managed download note.',
    ...overrides,
  }
}
