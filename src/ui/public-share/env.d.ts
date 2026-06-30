/// <reference types="vite/client" />

import type { PublicShareBootstrapPayload } from './types'

declare global {
  interface FileSystemWritableFileStream {
    write(data: BufferSource | Blob | string): Promise<void>
    close(): Promise<void>
    abort(): Promise<void>
  }

  interface FileSystemFileHandle {
    createWritable(): Promise<FileSystemWritableFileStream>
  }

  type SaveFilePickerOptions = {
    suggestedName?: string
  }

  interface Window {
    __IFS_PUBLIC_SHARE__?: PublicShareBootstrapPayload
    showSaveFilePicker?: (options?: SaveFilePickerOptions) => Promise<FileSystemFileHandle>
  }
}

export {}
