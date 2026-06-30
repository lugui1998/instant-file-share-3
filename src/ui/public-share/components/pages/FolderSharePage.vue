<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import type { PublicShareFolderModel, PublicSharePageModel } from '../../types'

const props = defineProps<{
  page: PublicSharePageModel
  folder: PublicShareFolderModel
}>()

const liveFolder = ref<PublicShareFolderModel>(props.folder)
let socket: WebSocket | null = null
let refreshInFlight = false
let refreshQueued = false

onMounted(() => {
  if (typeof WebSocket === 'undefined') {
    return
  }

  socket = new WebSocket(buildFolderUrl('folder-events', true))
  socket.addEventListener('message', () => {
    void refreshFolder()
  })
})

onUnmounted(() => {
  socket?.close()
  socket = null
})

async function refreshFolder() {
  if (refreshInFlight) {
    refreshQueued = true
    return
  }

  refreshInFlight = true
  try {
    const response = await fetch(buildFolderUrl('folder-list', false), {
      cache: 'no-store',
      headers: {
        Accept: 'application/json',
      },
    })

    if (response.ok) {
      liveFolder.value = await response.json() as PublicShareFolderModel
    }
  } finally {
    refreshInFlight = false
    if (refreshQueued) {
      refreshQueued = false
      void refreshFolder()
    }
  }
}

function buildFolderUrl(mode: 'folder-events' | 'folder-list', websocket: boolean) {
  const url = new URL(window.location.href)
  url.searchParams.set('ifs', mode)

  if (websocket) {
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  }

  return url.toString()
}

function buildDirectDownloadHref(href: string) {
  const url = new URL(href, window.location.href)
  url.searchParams.set('download', 'raw')

  return url.origin === window.location.origin
    ? `${url.pathname}${url.search}${url.hash}`
    : url.toString()
}
</script>

<template>
  <section class="stack">
    <nav
      v-if="liveFolder.breadcrumbs.length > 1"
      class="breadcrumbs"
      aria-label="Folder path"
    >
      <a
        v-for="breadcrumb in liveFolder.breadcrumbs"
        :key="breadcrumb.href"
        :href="breadcrumb.href"
      >
        {{ breadcrumb.label }}
      </a>
    </nav>

    <div v-if="liveFolder.isEmpty" class="empty-state">
      <p>This folder is empty.</p>
    </div>

    <table v-else class="folder-table">
      <thead>
        <tr>
          <th>Name</th>
          <th>Size</th>
          <th>Modified</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="entry in liveFolder.entries"
          :key="entry.href"
        >
          <td>
            <a :href="entry.href">
              {{ entry.name }}<span v-if="entry.isDirectory && !entry.isParentDirectory">/</span>
            </a>
          </td>
          <td>{{ entry.sizeLabel ?? '' }}</td>
          <td>{{ entry.modifiedAtLabel }}</td>
          <td>
            <a
              v-if="!entry.isDirectory"
              :href="buildDirectDownloadHref(entry.href)"
              :download="entry.name"
              class="folder-download-button"
              :aria-label="`Download ${entry.name}`"
            >
              <svg aria-hidden="true" viewBox="0 0 16 16">
                <path
                  d="M8 1.75a.75.75 0 0 1 .75.75v5.18l1.72-1.72a.75.75 0 1 1 1.06 1.06l-3 3a.75.75 0 0 1-1.06 0l-3-3a.75.75 0 1 1 1.06-1.06l1.72 1.72V2.5A.75.75 0 0 1 8 1.75Zm-4.5 8a.75.75 0 0 1 .75.75v1.25c0 .41.34.75.75.75h6c.41 0 .75-.34.75-.75V10.5a.75.75 0 0 1 1.5 0v1.25A2.25 2.25 0 0 1 11 14H5a2.25 2.25 0 0 1-2.25-2.25V10.5a.75.75 0 0 1 .75-.75Z"
                  fill="currentColor"
                />
              </svg>
            </a>
          </td>
        </tr>
      </tbody>
    </table>
  </section>
</template>
