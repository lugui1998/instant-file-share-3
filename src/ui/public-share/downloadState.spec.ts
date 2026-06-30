import { describe, expect, it } from 'vitest'
import {
  canPauseManagedDownload,
  canResumeManagedDownload,
  canUseBlobManagedDownload,
  createManagedDownloadState,
  blobManagedDownloadMaxBytes,
  reduceManagedDownloadState,
  requiresStreamingManagedDownload,
} from './downloadState'

describe('managed download state', () => {
  it('tracks planning, progress, retry, saving, and completion', () => {
    let state = reduceManagedDownloadState(createManagedDownloadState(), { type: 'start' })
    state = reduceManagedDownloadState(state, { type: 'plan-ready', totalBytes: 10 })
    state = reduceManagedDownloadState(state, { type: 'chunk-progress', chunkIndex: 0, downloadedBytes: 4 })
    state = reduceManagedDownloadState(state, { type: 'chunk-retry', chunkIndex: 1, retryCount: 2 })
    state = reduceManagedDownloadState(state, { type: 'chunk-progress', chunkIndex: 1, downloadedBytes: 10 })
    state = reduceManagedDownloadState(state, { type: 'saving' })
    state = reduceManagedDownloadState(state, { type: 'complete' })

    expect(state).toMatchObject({
      status: 'complete',
      totalBytes: 10,
      downloadedBytes: 10,
      activeChunkIndex: 1,
      retryCount: 0,
      error: null,
    })
  })

  it('pauses and resumes without losing completed bytes', () => {
    let state = reduceManagedDownloadState(createManagedDownloadState(), { type: 'start' })
    state = reduceManagedDownloadState(state, { type: 'plan-ready', totalBytes: 20 })
    state = reduceManagedDownloadState(state, { type: 'chunk-progress', chunkIndex: 0, downloadedBytes: 5 })

    expect(canPauseManagedDownload(state)).toBe(true)

    state = reduceManagedDownloadState(state, { type: 'pause' })

    expect(state.status).toBe('paused')
    expect(state.downloadedBytes).toBe(5)
    expect(canResumeManagedDownload(state)).toBe(true)

    state = reduceManagedDownloadState(state, { type: 'resume' })

    expect(state.status).toBe('downloading')
    expect(state.downloadedBytes).toBe(5)
  })

  it('moves to failed after retry exhaustion and allows resume', () => {
    let state = reduceManagedDownloadState(createManagedDownloadState(), { type: 'start' })
    state = reduceManagedDownloadState(state, { type: 'plan-ready', totalBytes: 20 })
    state = reduceManagedDownloadState(state, { type: 'chunk-retry', chunkIndex: 0, retryCount: 3 })
    state = reduceManagedDownloadState(state, { type: 'fail', error: 'Chunk 0 failed integrity verification.' })

    expect(state.status).toBe('failed')
    expect(state.retryCount).toBe(3)
    expect(state.error).toBe('Chunk 0 failed integrity verification.')
    expect(canResumeManagedDownload(state)).toBe(true)
  })

  it('requires streaming above the documented Blob assembly limit', () => {
    expect(canUseBlobManagedDownload(blobManagedDownloadMaxBytes)).toBe(true)
    expect(requiresStreamingManagedDownload(blobManagedDownloadMaxBytes)).toBe(false)
    expect(canUseBlobManagedDownload(blobManagedDownloadMaxBytes + 1)).toBe(false)
    expect(requiresStreamingManagedDownload(blobManagedDownloadMaxBytes + 1)).toBe(true)
  })
})
