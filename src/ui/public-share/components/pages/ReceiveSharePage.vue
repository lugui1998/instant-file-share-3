<script setup lang="ts">
import { computed, ref } from 'vue'
import type {
  PublicReceiveUploadResponse,
  PublicSharePageModel,
  PublicShareReceiveModel,
} from '../../types'

type UploadState = 'queued' | 'uploading' | 'success' | 'error' | 'canceled'

type UploadEntry = {
  id: string
  file: File
  relativePath: string
  progress: number
  state: UploadState
  message: string
  storedRelativePath?: string | null
  request?: XMLHttpRequest | null
}

type UploadListItem =
  | { type: 'file'; key: string; entry: UploadEntry }
  | {
      type: 'folder'
      key: string
      label: string
      entries: UploadEntry[]
      progress: number
      state: UploadState
      expanded: boolean
    }

const props = defineProps<{
  page: PublicSharePageModel
  receive: PublicShareReceiveModel
}>()

const emit = defineEmits<{
  quotaLabelChange: [label: string]
}>()

const uploadEntries = ref<UploadEntry[]>([])
const isDragActive = ref(false)
const isProcessingQueue = ref(false)
const summaryMessage = ref('')
const filePicker = ref<HTMLInputElement | null>(null)
const expandedFolders = ref<Set<string>>(new Set())

const totalBytes = computed(() => uploadEntries.value.reduce((sum, entry) => sum + entry.file.size, 0))
const completedBytes = computed(() =>
  uploadEntries.value.reduce((sum, entry) => sum + entry.file.size * (entry.progress / 100), 0),
)
const overallProgress = computed(() => totalBytes.value > 0 ? Math.min(100, (completedBytes.value / totalBytes.value) * 100) : 0)
const uploadListItems = computed<UploadListItem[]>(() => {
  const items: UploadListItem[] = []
  const folderItems = new Map<string, UploadEntry[]>()
  const folderOrder: string[] = []

  for (const entry of uploadEntries.value) {
    const folderName = getTopLevelFolder(entry.relativePath)
    if (!folderName) {
      items.push({ type: 'file', key: entry.id, entry })
      continue
    }

    if (!folderItems.has(folderName)) {
      folderItems.set(folderName, [])
      folderOrder.push(folderName)
    }

    folderItems.get(folderName)!.push(entry)
  }

  for (const folderName of folderOrder) {
    const entries = folderItems.get(folderName)!
    items.push({
      type: 'folder',
      key: `folder:${folderName}`,
      label: folderName,
      entries,
      progress: calculateGroupProgress(entries),
      state: calculateGroupState(entries),
      expanded: expandedFolders.value.has(folderName),
    })
  }

  return items
})

function createUploadEntries(files: Array<{ file: File; relativePath: string }>) {
  const createdAt = Date.now()
  return files.map((entry, index) => ({
    id: `${createdAt}-${index}-${entry.relativePath}`,
    file: entry.file,
    relativePath: entry.relativePath,
    progress: 0,
    state: 'queued' as UploadState,
    message: '',
    storedRelativePath: null,
    request: null,
  }))
}

function addUploadEntries(files: Array<{ file: File; relativePath: string }>) {
  if (!files.length) {
    return
  }

  uploadEntries.value.push(...createUploadEntries(files))
  summaryMessage.value = ''
  void startUploadQueue()
}

function queueLooseFiles(fileList: FileList | null) {
  if (!fileList?.length) {
    return
  }

  addUploadEntries(
    Array.from(fileList, (file) => ({
      file,
      relativePath: file.webkitRelativePath || file.name,
    })),
  )
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

  addUploadEntries(collectedFiles)
}

function openFilePicker() {
  filePicker.value?.click()
}

function handleFilePicker(event: Event) {
  const input = event.target as HTMLInputElement
  queueLooseFiles(input.files)
  input.value = ''
}

async function startUploadQueue() {
  if (isProcessingQueue.value) {
    return
  }

  isProcessingQueue.value = true
  try {
    while (true) {
      const nextEntry = uploadEntries.value.find((entry) => entry.state === 'queued')
      if (!nextEntry) {
        return
      }

      await uploadEntry(nextEntry)
    }
  } finally {
    isProcessingQueue.value = false
  }
}

async function uploadEntry(entry: UploadEntry) {
  entry.progress = 0
  entry.state = 'uploading'
  entry.message = 'Uploading...'
  entry.storedRelativePath = null

  const formData = new FormData()
  formData.append('files', entry.file, entry.file.name)
  formData.append('relativePaths', entry.relativePath)

  try {
    const response = await sendUploadRequest(entry, formData)
    applyUploadResult(entry, response)
  } catch (cause) {
    if (isCanceled(entry)) {
      return
    }

    entry.state = 'error'
    entry.message = cause instanceof Error ? cause.message : 'Upload failed.'
    summaryMessage.value = entry.message
  } finally {
    entry.request = null
  }
}

function sendUploadRequest(entry: UploadEntry, formData: FormData) {
  return new Promise<PublicReceiveUploadResponse>((resolve, reject) => {
    const request = new XMLHttpRequest()
    entry.request = request
    request.open('POST', props.receive.uploadUrl, true)
    request.responseType = 'json'

    request.upload.addEventListener('progress', (event) => {
      if (!event.lengthComputable || entry.state !== 'uploading') {
        return
      }

      entry.progress = Math.min(100, entry.file.size > 0 ? (event.loaded / entry.file.size) * 100 : 100)
    })

    request.addEventListener('load', () => {
      if (request.status < 200 || request.status >= 300) {
        reject(new Error(`Upload failed with status ${request.status}.`))
        return
      }

      if (!request.response) {
        reject(new Error('Upload failed.'))
        return
      }

      resolve(request.response as PublicReceiveUploadResponse)
    })

    request.addEventListener('abort', () => {
      reject(new Error('Upload stopped.'))
    })

    request.addEventListener('error', () => {
      reject(new Error('Upload failed.'))
    })

    request.send(formData)
  })
}

function applyUploadResult(entry: UploadEntry, response: PublicReceiveUploadResponse) {
  emit('quotaLabelChange', props.receive.remainingQuotaLabel === 'Unlimited' ? 'Unlimited' : formatBytes(response.remainingQuotaBytes))
  const result = response.results.find((candidate) => candidate.relativePath === entry.relativePath)

  if (!result) {
    entry.state = 'error'
    entry.message = 'No server result was returned for this file.'
    summaryMessage.value = entry.message
    return
  }

  entry.progress = 100
  entry.state = result.success ? 'success' : 'error'
  entry.message = result.success
    ? (result.storedRelativePath ? `Saved as ${result.storedRelativePath}` : 'Saved')
    : (result.message || 'Upload failed.')
  entry.storedRelativePath = result.storedRelativePath

  if (!result.success) {
    summaryMessage.value = entry.message
  }
}

function stopEntry(entry: UploadEntry) {
  if (entry.state !== 'uploading' && entry.state !== 'queued') {
    return
  }

  entry.state = 'canceled'
  entry.message = 'Stopped'
  entry.request?.abort()
}

function restartEntry(entry: UploadEntry) {
  if (entry.state === 'uploading' || entry.state === 'queued') {
    return
  }

  entry.progress = 0
  entry.state = 'queued'
  entry.message = ''
  entry.storedRelativePath = null
  summaryMessage.value = ''
  void startUploadQueue()
}

function stopGroup(entries: UploadEntry[]) {
  for (const entry of entries) {
    stopEntry(entry)
  }
}

function restartGroup(entries: UploadEntry[]) {
  for (const entry of entries) {
    if (canRestartEntry(entry)) {
      restartEntry(entry)
    }
  }
}

function toggleFolder(folderName: string) {
  const nextExpandedFolders = new Set(expandedFolders.value)
  if (nextExpandedFolders.has(folderName)) {
    nextExpandedFolders.delete(folderName)
  } else {
    nextExpandedFolders.add(folderName)
  }

  expandedFolders.value = nextExpandedFolders
}

function handleGroupAction(item: Extract<UploadListItem, { type: 'folder' }>) {
  if (canStopGroup(item.entries)) {
    stopGroup(item.entries)
    return
  }

  restartGroup(item.entries)
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

function getTopLevelFolder(relativePath: string) {
  const normalizedPath = relativePath.replaceAll('\\', '/')
  const separatorIndex = normalizedPath.indexOf('/')
  return separatorIndex > 0 ? normalizedPath.slice(0, separatorIndex) : null
}

function calculateGroupProgress(entries: UploadEntry[]) {
  const groupBytes = entries.reduce((sum, entry) => sum + entry.file.size, 0)
  if (groupBytes <= 0) {
    return entries.every((entry) => entry.state === 'success') ? 100 : 0
  }

  const uploadedBytes = entries.reduce((sum, entry) => sum + entry.file.size * (entry.progress / 100), 0)
  return Math.min(100, (uploadedBytes / groupBytes) * 100)
}

function calculateGroupState(entries: UploadEntry[]): UploadState {
  if (entries.some((entry) => entry.state === 'uploading')) {
    return 'uploading'
  }

  if (entries.some((entry) => entry.state === 'queued')) {
    return 'queued'
  }

  if (entries.some((entry) => entry.state === 'error')) {
    return 'error'
  }

  if (entries.some((entry) => entry.state === 'canceled')) {
    return 'canceled'
  }

  return 'success'
}

function canStopEntry(entry: UploadEntry) {
  return entry.state === 'uploading' || entry.state === 'queued'
}

function canRestartEntry(entry: UploadEntry) {
  return entry.state === 'error' || entry.state === 'canceled'
}

function isCanceled(entry: UploadEntry) {
  return entry.state === 'canceled'
}

function canStopGroup(entries: UploadEntry[]) {
  return entries.some(canStopEntry)
}

function canRestartGroup(entries: UploadEntry[]) {
  return entries.some(canRestartEntry)
}

function getEntryActionLabel(entry: UploadEntry) {
  if (canStopEntry(entry)) {
    return 'Stop'
  }

  return canRestartEntry(entry) ? 'Restart' : ''
}

function getGroupActionLabel(entries: UploadEntry[]) {
  if (canStopGroup(entries)) {
    return 'Stop'
  }

  return canRestartGroup(entries) ? 'Restart' : ''
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
    <div
      class="upload-dropzone"
      :class="{ 'upload-dropzone--active': isDragActive }"
      role="button"
      tabindex="0"
      aria-label="Drop files or folders here, or click to select files."
      @click="openFilePicker"
      @keydown.enter.prevent="openFilePicker"
      @keydown.space.prevent="openFilePicker"
      @dragenter.prevent="handleDragEnter"
      @dragover.prevent="handleDragEnter"
      @dragleave="handleDragLeave"
      @drop="handleDrop"
    >
      <input
        ref="filePicker"
        class="upload-picker-input"
        type="file"
        multiple
        tabindex="-1"
        aria-hidden="true"
        @change="handleFilePicker"
        @click.stop
      />
      <span class="upload-dropzone__icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" focusable="false">
          <path d="M12 15V4" />
          <path d="m7 9 5-5 5 5" />
          <path d="M5 15v3a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-3" />
        </svg>
      </span>
    </div>

    <div v-if="uploadEntries.length > 0" class="upload-panel">
      <div class="progress-summary">
        <strong>Overall progress</strong>
        <span>{{ formatProgress(overallProgress) }}</span>
      </div>
      <div class="progress-track">
        <div class="progress-fill" :style="{ width: `${overallProgress}%` }" />
      </div>
      <p v-if="summaryMessage" class="body-copy">{{ summaryMessage }}</p>

      <div class="upload-list">
        <template v-for="item in uploadListItems" :key="item.key">
          <article v-if="item.type === 'file'" class="upload-item">
            <div class="upload-item__row">
              <div>
                <strong>{{ item.entry.relativePath }}</strong>
                <p v-if="item.entry.message">{{ item.entry.message }}</p>
              </div>
              <div class="upload-item__actions">
                <span class="badge small-badge" :class="`badge--${item.entry.state}`">{{ formatProgress(item.entry.progress) }}</span>
                <button
                  v-if="getEntryActionLabel(item.entry)"
                  class="icon-text-button"
                  type="button"
                  @click="canStopEntry(item.entry) ? stopEntry(item.entry) : restartEntry(item.entry)"
                >
                  {{ getEntryActionLabel(item.entry) }}
                </button>
              </div>
            </div>
            <div class="progress-track compact-track">
              <div class="progress-fill" :style="{ width: `${item.entry.progress}%` }" />
            </div>
          </article>

          <article v-else class="upload-folder">
            <div class="upload-folder__header">
              <button class="folder-toggle" type="button" @click="toggleFolder(item.label)">
                <span class="folder-toggle__chevron">{{ item.expanded ? 'v' : '>' }}</span>
                <span>
                  <strong>{{ item.label }}</strong>
                  <small>{{ item.entries.length }} item{{ item.entries.length === 1 ? '' : 's' }}</small>
                </span>
              </button>
              <div class="upload-item__actions">
                <span class="badge small-badge" :class="`badge--${item.state}`">{{ formatProgress(item.progress) }}</span>
                <button
                  v-if="getGroupActionLabel(item.entries)"
                  class="icon-text-button"
                  type="button"
                  @click="handleGroupAction(item)"
                >
                  {{ getGroupActionLabel(item.entries) }}
                </button>
              </div>
            </div>
            <div class="progress-track compact-track">
              <div class="progress-fill" :style="{ width: `${item.progress}%` }" />
            </div>

            <div v-if="item.expanded" class="upload-folder__children">
              <div v-for="entry in item.entries" :key="entry.id" class="upload-child-row">
                <div>
                  <strong>{{ entry.relativePath.slice(item.label.length + 1) }}</strong>
                  <p v-if="entry.message">{{ entry.message }}</p>
                </div>
                <div class="upload-item__actions">
                  <span class="badge small-badge" :class="`badge--${entry.state}`">{{ formatProgress(entry.progress) }}</span>
                  <button
                    v-if="getEntryActionLabel(entry)"
                    class="icon-text-button"
                    type="button"
                    @click="canStopEntry(entry) ? stopEntry(entry) : restartEntry(entry)"
                  >
                    {{ getEntryActionLabel(entry) }}
                  </button>
                </div>
              </div>
            </div>
          </article>
        </template>
      </div>
    </div>
  </section>
</template>
