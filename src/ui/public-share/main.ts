import { createApp } from 'vue'
import PublicShareApp from './PublicShareApp.vue'
import './styles.css'
import type { PublicShareBootstrapPayload } from './types'

const payload: PublicShareBootstrapPayload = window.__IFS_PUBLIC_SHARE__ ?? {
  page: {
    kind: 'file',
    title: 'Unavailable',
    description: 'No public share payload was found.',
    canonicalUrl: window.location.href,
    siteName: 'Instant File Share',
    primaryActionLabel: null,
    primaryActionUrl: null,
    file: {
      fileName: 'Unavailable',
      displaySize: '0 B',
      preferInline: false,
      actionVerb: 'Download',
      actionLabel: 'The public share payload is missing.',
    },
    folder: null,
    zip: null,
  },
}

createApp(PublicShareApp, payload).mount('#public-share-app')
