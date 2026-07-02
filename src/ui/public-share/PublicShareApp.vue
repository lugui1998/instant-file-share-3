<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import PublicShareShell from './components/PublicShareShell.vue'
import FileSharePage from './components/pages/FileSharePage.vue'
import FolderSharePage from './components/pages/FolderSharePage.vue'
import ReceiveSharePage from './components/pages/ReceiveSharePage.vue'
import ZipSharePage from './components/pages/ZipSharePage.vue'
import type { PublicShareBootstrapPayload } from './types'

const props = defineProps<PublicShareBootstrapPayload>()

const receiveRemainingQuotaLabel = ref(props.page.receive?.remainingQuotaLabel ?? '')
const fileHeaderSizeLabel = computed(() =>
  props.page.kind === 'file' ? props.page.file?.displaySize ?? '' : '')

watch(
  () => props.page.receive?.remainingQuotaLabel,
  (label) => {
    receiveRemainingQuotaLabel.value = label ?? ''
  },
)
</script>

<template>
  <PublicShareShell :page="props.page">
    <template
      v-if="props.page.kind === 'receive' && props.page.receive"
      #header-meta
    >
      <div class="receive-header-meta">
        <span class="badge">
          {{ receiveRemainingQuotaLabel === 'Unlimited' ? 'Unlimited' : `${receiveRemainingQuotaLabel} left` }}
        </span>
        <span v-if="props.page.receive.expiresAtLabel" class="receive-meta">
          Expires {{ props.page.receive.expiresAtLabel }}
        </span>
      </div>
    </template>
    <template
      v-else-if="fileHeaderSizeLabel"
      #header-meta
    >
      <div class="file-header-meta">
        <span class="label">Size</span>
        <strong>{{ fileHeaderSizeLabel }}</strong>
      </div>
    </template>

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

    <ReceiveSharePage
      v-else-if="props.page.kind === 'receive' && props.page.receive"
      :page="props.page"
      :receive="props.page.receive"
      @quota-label-change="receiveRemainingQuotaLabel = $event"
    />

    <section v-else class="empty-state">
      <p>This share could not be rendered.</p>
    </section>
  </PublicShareShell>
</template>
