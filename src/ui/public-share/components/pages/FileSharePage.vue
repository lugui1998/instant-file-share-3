<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  downloadFileWithCompressionFallback,
  estimateBlobFallbackMemoryCost,
  formatTransferBytes,
  supportsGzipDecompression,
  supportsStreamingFileSave,
  type DownloadResult,
} from '../../compressionDownload'
import type { PublicShareFileModel, PublicSharePageModel } from '../../types'

const props = defineProps<{
  page: PublicSharePageModel
  file: PublicShareFileModel
}>()

const isDownloading = ref(false)
const downloadResult = ref<DownloadResult | null>(null)
const downloadError = ref<string | null>(null)

const canAttemptCompressedDownload = computed(() => props.file.canUseBrowserCompression && Boolean(props.file.compressedDownloadUrl))
const compressionSupportLabel = computed(() => {
  if (!canAttemptCompressedDownload.value) {
    return 'Raw download'
  }

  if (!supportsGzipDecompression()) {
    return 'Raw fallback'
  }

  return supportsStreamingFileSave() ? 'Compressed streaming' : 'Compressed Blob save'
})
const blobMemoryLabel = computed(() => {
  const estimate = estimateBlobFallbackMemoryCost(props.file.sizeBytes)
  return formatTransferBytes(estimate.minimumTransientBytes)
})

async function startDownload() {
  isDownloading.value = true
  downloadError.value = null

  try {
    downloadResult.value = await downloadFileWithCompressionFallback(props.file)
  } catch (error) {
    downloadError.value = error instanceof Error ? error.message : 'Download failed.'
  } finally {
    isDownloading.value = false
  }
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

    <div class="download-actions">
      <button
        class="public-shell__action"
        type="button"
        :disabled="isDownloading"
        @click="startDownload"
      >
        {{ isDownloading ? 'Preparing download...' : file.actionVerb + ' file' }}
      </button>

      <a class="secondary-action" :href="file.rawDownloadUrl">
        Raw fallback
      </a>
    </div>

    <p class="download-note" role="status">
      {{ compressionSupportLabel }}
      <template v-if="canAttemptCompressedDownload && !supportsStreamingFileSave()">
        - Blob path buffers about {{ blobMemoryLabel }} before saving.
      </template>
      <template v-if="downloadResult?.mode === 'raw-fallback' && downloadResult.reason">
        - {{ downloadResult.reason }}
      </template>
      <template v-if="downloadError">
        - {{ downloadError }}
      </template>
    </p>

    <div class="info-grid">
      <article class="info-card">
        <span class="label">Mode</span>
        <strong>{{ file.preferInline ? 'Open in browser when supported' : 'Download only' }}</strong>
      </article>

      <article class="info-card">
        <span class="label">Content type</span>
        <strong>{{ canAttemptCompressedDownload ? 'Raw or browser-decompressed gzip' : file.actionVerb + ' from this link' }}</strong>
      </article>
    </div>
  </section>
</template>
