<script setup lang="ts">
import type { PublicSharePageModel, PublicShareZipModel } from '../../types'

defineProps<{
  page: PublicSharePageModel
  zip: PublicShareZipModel
}>()
</script>

<template>
  <section class="stack">
    <div class="hero-panel">
      <div>
        <p class="label">ZIP archive</p>
        <h2>{{ zip.fileName }}.zip</h2>
      </div>

      <a
        v-if="page.primaryActionLabel && page.primaryActionUrl"
        class="secondary-action"
        :href="page.primaryActionUrl"
      >
        {{ page.primaryActionLabel }}
      </a>
    </div>

    <p class="body-copy">{{ zip.actionLabel }}</p>
  </section>
</template>
