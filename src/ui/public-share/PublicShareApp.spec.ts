import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import PublicShareApp from './PublicShareApp.vue'
import type { PublicShareBootstrapPayload } from './types'

describe('PublicShareApp', () => {
  it('shows file size as compact header metadata on file pages', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})))
    const wrapper = mount(PublicShareApp, {
      props: createFilePayload(),
    })

    expect(wrapper.find('.file-header-meta').text()).toContain('2.4 GB')
    expect(wrapper.find('.hero-panel').exists()).toBe(false)
  })
})

function createFilePayload(): PublicShareBootstrapPayload {
  return {
    page: {
      kind: 'file',
      title: 'A long video file.mkv',
      description: 'Download A long video file.mkv.',
      canonicalUrl: 'https://public.example/s/file-token',
      siteName: 'Instant File Share',
      repositoryUrl: 'https://github.com/lugui1998/instant-file-share-3',
      primaryActionLabel: null,
      primaryActionUrl: null,
      file: {
        fileName: 'A long video file.mkv',
        displaySize: '2.4 GB',
        sizeBytes: 2_400_000_000,
        preferInline: false,
        canUseBrowserCompression: false,
        rawDownloadUrl: 'https://public.example/s/file-token',
        compressedDownloadUrl: null,
        actionVerb: 'View',
        actionLabel: 'Open this link to view A long video file.mkv.',
        managedDownload: {
          manifestUrl: 'https://public.example/s/file-token?ifs=download-plan',
          rawDownloadUrl: 'https://public.example/s/file-token?download=raw',
          fileSizeBytes: 2_400_000_000,
          defaultChunkSizeBytes: 4 * 1024 * 1024,
          maxRetriesPerChunk: 3,
          maxMemoryBytes: 3_000_000_000,
          maxParallelChunks: 4,
          compressionMode: 'Auto',
          transferDiagnosticsEnabled: false,
          saveLimitationNote: 'Browser-managed download note.',
        },
      },
      folder: null,
      zip: null,
      receive: null,
    },
  }
}
