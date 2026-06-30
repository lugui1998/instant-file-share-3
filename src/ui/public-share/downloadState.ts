export type ManagedDownloadStatus = 'idle' | 'planning' | 'downloading' | 'paused' | 'saving' | 'complete' | 'failed'

// Above this size, buffering chunks plus a final Blob can briefly require more than 2x the file size.
export const blobManagedDownloadMaxBytes = 512 * 1024 * 1024
export const largeFileStreamingRequiredMessage =
  'This file is too large for in-memory browser assembly. Choose a save location when prompted, or use Direct download.'

export type ManagedDownloadState = {
  status: ManagedDownloadStatus
  totalBytes: number
  downloadedBytes: number
  activeChunkIndex: number
  retryCount: number
  error: string | null
}

export type ManagedDownloadEvent =
  | { type: 'start' }
  | { type: 'plan-ready', totalBytes: number }
  | { type: 'chunk-progress', chunkIndex: number, downloadedBytes: number }
  | { type: 'chunk-retry', chunkIndex: number, retryCount: number }
  | { type: 'pause' }
  | { type: 'resume' }
  | { type: 'saving' }
  | { type: 'complete' }
  | { type: 'fail', error: string }
  | { type: 'reset' }

export function createManagedDownloadState(): ManagedDownloadState {
  return {
    status: 'idle',
    totalBytes: 0,
    downloadedBytes: 0,
    activeChunkIndex: 0,
    retryCount: 0,
    error: null,
  }
}

export function reduceManagedDownloadState(
  state: ManagedDownloadState,
  event: ManagedDownloadEvent,
): ManagedDownloadState {
  switch (event.type) {
    case 'start':
      return {
        ...createManagedDownloadState(),
        status: 'planning',
      }
    case 'plan-ready':
      return {
        ...state,
        status: 'downloading',
        totalBytes: Math.max(0, event.totalBytes),
        error: null,
      }
    case 'chunk-progress':
      return {
        ...state,
        status: state.status === 'paused' ? 'paused' : 'downloading',
        activeChunkIndex: event.chunkIndex,
        downloadedBytes: Math.min(state.totalBytes, Math.max(0, event.downloadedBytes)),
        error: null,
      }
    case 'chunk-retry':
      return {
        ...state,
        status: 'downloading',
        activeChunkIndex: event.chunkIndex,
        retryCount: event.retryCount,
        error: null,
      }
    case 'pause':
      return state.status === 'downloading' || state.status === 'planning'
        ? { ...state, status: 'paused' }
        : state
    case 'resume':
      return state.status === 'paused' || state.status === 'failed'
        ? { ...state, status: 'downloading', error: null }
        : state
    case 'saving':
      return {
        ...state,
        status: 'saving',
        downloadedBytes: state.totalBytes,
        error: null,
      }
    case 'complete':
      return {
        ...state,
        status: 'complete',
        downloadedBytes: state.totalBytes,
        retryCount: 0,
        error: null,
      }
    case 'fail':
      return {
        ...state,
        status: 'failed',
        error: event.error,
      }
    case 'reset':
      return createManagedDownloadState()
  }
}

export function canPauseManagedDownload(state: ManagedDownloadState) {
  return state.status === 'planning' || state.status === 'downloading'
}

export function canResumeManagedDownload(state: ManagedDownloadState) {
  return state.status === 'paused' || state.status === 'failed'
}

export function requiresStreamingManagedDownload(fileSizeBytes: number) {
  return fileSizeBytes > blobManagedDownloadMaxBytes
}

export function canUseBlobManagedDownload(fileSizeBytes: number) {
  return !requiresStreamingManagedDownload(fileSizeBytes)
}
