<script setup lang="ts">
import { computed, ref } from 'vue'
import type { BrowserTransferEncryptedDownloadPlan, PublicShareFileModel, PublicSharePageModel } from '../../types'
import {
  base64UrlDecode,
  decryptBrowserTransferChunk,
  importBrowserTransferKey,
  parseBrowserTransferKeyFragment,
} from '../../crypto/browserTransferCrypto'

const props = defineProps<{
  page: PublicSharePageModel
  file: PublicShareFileModel
}>()

const encryptionStatus = ref('')
const encryptionState = ref<'idle' | 'decrypting' | 'complete' | 'error'>('idle')
const encryptionFragment = computed(() =>
  props.file.encryptionExperiment ? parseBrowserTransferKeyFragment(window.location.hash) : null)

async function startEncryptedDownload() {
  const experiment = props.file.encryptionExperiment
  if (!experiment) {
    return
  }

  const fragment = encryptionFragment.value
  if (!fragment || fragment.mode === 'upload') {
    encryptionState.value = 'error'
    encryptionStatus.value = 'Missing download key. Add an AES-GCM key in the URL fragment before trying encrypted download decryption.'
    return
  }

  encryptionState.value = 'decrypting'
  encryptionStatus.value = 'Fetching encrypted payload...'

  try {
    const planResponse = await fetch(experiment.downloadManifestUrl, { cache: 'no-store' })
    if (!planResponse.ok) {
      throw new Error(`Encrypted download plan failed with HTTP ${planResponse.status}.`)
    }

    const plan = await planResponse.json() as BrowserTransferEncryptedDownloadPlan
    const encryptedResponse = await fetch(plan.encryptedDownloadUrl, { cache: 'no-store' })
    if (!encryptedResponse.ok) {
      throw new Error(`Encrypted download failed with HTTP ${encryptedResponse.status}.`)
    }

    const key = await importBrowserTransferKey(fragment.keyBytes)
    const plaintext = await decryptBrowserTransferChunk(key, {
      iv: base64UrlDecode(plan.ivBase64Url),
      ciphertext: new Uint8Array(await encryptedResponse.arrayBuffer()),
    })
    saveBlob(plan.fileName, plan.contentType, [plaintext])
    encryptionState.value = 'complete'
    encryptionStatus.value = 'Encrypted download decrypted in the browser.'
  } catch (error) {
    encryptionState.value = 'error'
    encryptionStatus.value = error instanceof Error ? error.message : 'Encrypted download decryption failed.'
  }
}

function saveBlob(fileName: string, contentType: string, parts: BlobPart[]) {
  const blob = new Blob(parts, { type: contentType || 'application/octet-stream' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  anchor.rel = 'noopener'
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
}
</script>

<template>
  <section class="stack">
    <div class="hero-panel">
      <div>
        <p class="label">File</p>
        <h2>{{ file.fileName }}</h2>
      </div>

      <span class="badge">{{ file.displaySize }}</span>
    </div>

    <p class="body-copy">{{ file.actionLabel }}</p>

    <div v-if="file.encryptionExperiment" class="download-panel">
      <div class="download-panel__header">
        <div>
          <span class="label">Encryption experiment</span>
          <strong>{{ file.encryptionExperiment.algorithm }} fragment-key decrypt</strong>
        </div>
      </div>

      <p v-if="encryptionStatus" class="body-copy">{{ encryptionStatus }}</p>

      <div class="download-actions">
        <button
          class="icon-text-button"
          type="button"
          :disabled="encryptionState === 'decrypting'"
          @click="startEncryptedDownload"
        >
          {{ encryptionState === 'decrypting' ? 'Decrypting...' : 'Try encrypted download' }}
        </button>
      </div>
    </div>

    <div class="info-grid">
      <article class="info-card">
        <span class="label">Mode</span>
        <strong>{{ file.preferInline ? 'Open in browser when supported' : 'Download only' }}</strong>
      </article>

      <article class="info-card">
        <span class="label">Content type</span>
        <strong>{{ file.actionVerb }} from this link</strong>
      </article>
    </div>
  </section>
</template>
