/// <reference types="vite/client" />

import type { PublicShareBootstrapPayload } from './types'

declare global {
  interface Window {
    __IFS_PUBLIC_SHARE__?: PublicShareBootstrapPayload
  }
}

export {}
