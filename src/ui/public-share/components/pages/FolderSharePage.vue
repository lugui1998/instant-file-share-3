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
        </tr>
      </tbody>
    </table>
  </section>
</template>
