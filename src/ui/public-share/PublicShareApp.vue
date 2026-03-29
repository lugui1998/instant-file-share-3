<script setup lang="ts">
import PublicShareShell from './components/PublicShareShell.vue'
import FileSharePage from './components/pages/FileSharePage.vue'
import FolderSharePage from './components/pages/FolderSharePage.vue'
import ZipSharePage from './components/pages/ZipSharePage.vue'
import type { PublicShareBootstrapPayload } from './types'

const props = defineProps<PublicShareBootstrapPayload>()
</script>

<template>
  <PublicShareShell :page="props.page">
    <FileSharePage
      v-if="props.page.kind === 'file' && props.page.file"
      :page="props.page"
      :file="props.page.file"
    />

    <FolderSharePage
      v-else-if="props.page.kind === 'folder' && props.page.folder"
      :page="props.page"
      :folder="props.page.folder"
    />

    <ZipSharePage
      v-else-if="props.page.kind === 'zip' && props.page.zip"
      :page="props.page"
      :zip="props.page.zip"
    />

    <section v-else class="empty-state">
      <p>This share could not be rendered.</p>
    </section>
  </PublicShareShell>
</template>
