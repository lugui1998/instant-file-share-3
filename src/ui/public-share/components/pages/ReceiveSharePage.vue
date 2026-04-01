<script setup lang="ts">
import { computed, ref } from 'vue'
import type {
  PublicReceiveUploadResponse,
  PublicSharePageModel,
  PublicShareReceiveModel,
} from '../../types'

type UploadState = 'queued' | 'uploading' | 'success' | 'error'

type UploadEntry = {
  id: string
  file: File
  relativePath: string
  progress: number
  state: UploadState
  message: string
  storedRelativePath?: string | null
}

const props = defineProps<{
  page: PublicSharePageModel
  receive: PublicShareReceiveModel
}>()

const queuedUploads = ref<UploadEntry[]>([])
const isUploading = ref(false)
const isDragActive = ref(false)
const overallProgress = ref(0)
const summaryMessage = ref('')
const activeRequest = ref<XMLHttpRequest | null>(null)
const remainingQuotaLabel = ref(props.receive.remainingQuotaLabel)

const totalBytes = computed(() => queuedUploads.value.reduce((sum, entry) => sum + entry.file.size, 0))

function createUploadEntries(files: Array<{ file: File; relativePath: string }>) {
  return files.map((entry, index) => ({
    id: `${Date.now()}-${index}-${entry.relativePath}`,
    file: entry.file,
    relativePath: entry.relativePath,
    progress: 0,
    state: 'queued' as UploadState,
    message: 'Ready',
    storedRelativePath: null,
  }))
}

function queueLooseFiles(fileList: FileList | null) {
  if (!fileList?.length) {
    return
  }

  queuedUploads.value = createUploadEntries(
    Array.from(fileList, (file) => ({
      file,
      relativePath: file.webkitRelativePath || file.name,
    })),
  )
  summaryMessage.value = ''
}

async function queueDroppedItems(event: DragEvent) {
  const items = Array.from(event.dataTransfer?.items ?? [])
  if (!items.length) {
    queueLooseFiles(event.dataTransfer?.files ?? null)
    return
  }

  const collectedFiles = (await Promise.all(items.map(collectDroppedFiles))).flat()
  if (collectedFiles.length === 0) {
    queueLooseFiles(event.dataTransfer?.files ?? null)
    return
  }

  queuedUploads.value = createUploadEntries(collectedFiles)
  summaryMessage.value = ''
}

function handleFilePicker(event: Event) {
  queueLooseFiles((event.target as HTMLInputElement).files)
}

function handleFolderPicker(event: Event) {
  queueLooseFiles((event.target as HTMLInputElement).files)
}

function updateProgress(totalLoaded: number) {
  overallProgress.value = totalBytes.value > 0 ? Math.min(100, (totalLoaded / totalBytes.value) * 100) : 0

  let remainingLoaded = totalLoaded
  for (const entry of queuedUploads.value) {
    if (entry.state === 'success') {
      entry.progress = 100
      continue
    }

    if (remainingLoaded <= 0) {
      entry.progress = entry.state === 'error' ? entry.progress : 0
      continue
    }

    const applied = Math.min(entry.file.size, remainingLoaded)
    entry.progress = Math.min(100, entry.file.size > 0 ? (applied / entry.file.size) * 100 : 100)
    remainingLoaded -= applied
  }
}

async function uploadFiles() {
  if (isUploading.value || queuedUploads.value.length === 0) {
    return
  }

  isUploading.value = true
  overallProgress.value = 0
  summaryMessage.value = ''

  for (const entry of queuedUploads.value) {
    entry.progress = 0
    entry.state = 'uploading'
    entry.message = 'Uploading...'
    entry.storedRelativePath = null
  }

  const formData = new FormData()
  for (const entry of queuedUploads.value) {
    formData.append('files', entry.file, entry.file.name)
    formData.append('relativePaths', entry.relativePath)
  }

  try {
    const response = await sendUploadRequest(formData)
    applyUploadResults(response)
    summaryMessage.value = response.failedCount > 0
      ? `Uploaded ${response.uploadedCount} item(s); ${response.failedCount} failed.`
      : `Uploaded ${response.uploadedCount} item(s) successfully.`
  } catch (cause) {
    const message = cause instanceof Error ? cause.message : 'Upload failed.'
    summaryMessage.value = message
    for (const entry of queuedUploads.value) {
      if (entry.state === 'uploading') {
        entry.state = 'error'
        entry.message = message
      }
    }
  } finally {
    isUploading.value = false
    activeRequest.value = null
  }
}

function sendUploadRequest(formData: FormData) {
  return new Promise<PublicReceiveUploadResponse>((resolve, reject) => {
    const request = new XMLHttpRequest()
    activeRequest.value = request
    request.open('POST', props.receive.uploadUrl, true)
    request.responseType = 'json'

    request.upload.addEventListener('progress', (event) => {
      if (!event.lengthComputable) {
        return
      }

      updateProgress(event.loaded)
    })

    request.addEventListener('load', () => {
      if (request.status < 200 || request.status >= 300) {
        reject(new Error(`Upload failed with status ${request.status}.`))
        return
      }

      resolve(request.response as PublicReceiveUploadResponse)
    })

    request.addEventListener('error', () => {
      reject(new Error('Upload failed.'))
    })

    request.send(formData)
  })
}

function applyUploadResults(response: PublicReceiveUploadResponse) {
  overallProgress.value = 100
  remainingQuotaLabel.value = formatBytes(response.remainingQuotaBytes)
  const resultsByPath = new Map(response.results.map((result) => [result.relativePath, result]))

  for (const entry of queuedUploads.value) {
    const result = resultsByPath.get(entry.relativePath)
    if (!result) {
      entry.state = 'error'
      entry.message = 'No server result was returned for this file.'
      continue
    }

    entry.progress = 100
    entry.state = result.success ? 'success' : 'error'
    entry.message = result.success
      ? (result.storedRelativePath ? `Saved as ${result.storedRelativePath}` : 'Saved')
      : (result.message || 'Upload failed.')
    entry.storedRelativePath = result.storedRelativePath
  }
}

function handleDragEnter() {
  isDragActive.value = true
}

function handleDragLeave(event: DragEvent) {
  if (!event.currentTarget || !(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null)) {
    isDragActive.value = false
  }
}

async function handleDrop(event: DragEvent) {
  event.preventDefault()
  isDragActive.value = false
  await queueDroppedItems(event)
}

function formatProgress(progress: number) {
  return `${Math.round(progress)}%`
}

function formatBytes(value: number) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let normalized = Math.max(0, value)
  let unitIndex = 0

  while (normalized >= 1024 && unitIndex < units.length - 1) {
    normalized /= 1024
    unitIndex++
  }

  return unitIndex === 0 ? `${Math.round(normalized)} ${units[unitIndex]}` : `${normalized.toFixed(1).replace(/\.0$/, '')} ${units[unitIndex]}`
}

async function collectDroppedFiles(item: DataTransferItem): Promise<Array<{ file: File; relativePath: string }>> {
  const entry = (item as DataTransferItem & { webkitGetAsEntry?: () => any }).webkitGetAsEntry?.()
  if (!entry) {
    const file = item.getAsFile()
    return file ? [{ file, relativePath: file.name }] : []
  }

  return readEntry(entry, '')
}

async function readEntry(entry: any, parentPath: string): Promise<Array<{ file: File; relativePath: string }>> {
  if (entry.isFile) {
    const file = await new Promise<File>((resolve, reject) => entry.file(resolve, reject))
    return [{ file, relativePath: `${parentPath}${entry.name}` }]
  }

  if (!entry.isDirectory) {
    return []
  }

  const reader = entry.createReader()
  const children: any[] = []
  while (true) {
    const batch = await new Promise<any[]>((resolve, reject) => reader.readEntries(resolve, reject))
    if (batch.length === 0) {
      break
    }

    children.push(...batch)
  }

  const childFiles = await Promise.all(children.map((child) => readEntry(child, `${parentPath}${entry.name}/`)))
  return childFiles.flat()
}
</script>

<template>
  <section class="stack">
    <div class="hero-panel">
      <div>
        <p class="label">Receive</p>
        <h2>{{ receive.targetName }}</h2>
      </div>

      <div class="stack compact-stack">
        <span class="badge">{{ remainingQuotaLabel === 'Unlimited' ? 'Unlimited' : `${remainingQuotaLabel} left` }}</span>
        <span v-if="receive.expiresAtLabel" class="receive-meta">Expires {{ receive.expiresAtLabel }}</span>
      </div>
    </div>

    <div
      class="upload-dropzone"
      :class="{ 'upload-dropzone--active': isDragActive }"
      @dragenter.prevent="handleDragEnter"
      @dragover.prevent="handleDragEnter"
      @dragleave="handleDragLeave"
      @drop="handleDrop"
    >
      <p>Drop files or folders here, or use the picker buttons below.</p>
      <div class="upload-actions">
        <label class="secondary-action upload-picker">
          Choose files
          <input type="file" multiple @change="handleFilePicker" />
        </label>
        <label class="secondary-action upload-picker">
          Choose folder
          <input type="file" multiple webkitdirectory @change="handleFolderPicker" />
        </label>
        <button class="public-shell__action" type="button" :disabled="isUploading || queuedUploads.length === 0" @click="uploadFiles">
          {{ isUploading ? 'Uploading...' : 'Upload batch' }}
        </button>
      </div>
    </div>

    <div v-if="queuedUploads.length > 0" class="upload-panel">
      <div class="progress-summary">
        <strong>Overall progress</strong>
        <span>{{ formatProgress(overallProgress) }}</span>
      </div>
      <div class="progress-track">
        <div class="progress-fill" :style="{ width: `${overallProgress}%` }" />
      </div>
      <p v-if="summaryMessage" class="body-copy">{{ summaryMessage }}</p>

      <div class="upload-list">
        <article v-for="entry in queuedUploads" :key="entry.id" class="upload-item">
          <div class="upload-item__row">
            <div>
              <strong>{{ entry.relativePath }}</strong>
              <p>{{ entry.message }}</p>
            </div>
            <span class="badge small-badge" :class="`badge--${entry.state}`">{{ formatProgress(entry.progress) }}</span>
          </div>
          <div class="progress-track compact-track">
            <div class="progress-fill" :style="{ width: `${entry.progress}%` }" />
          </div>
        </article>
      </div>
    </div>
  </section>
</template>
