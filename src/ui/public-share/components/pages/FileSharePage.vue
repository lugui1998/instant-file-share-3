<script setup lang="ts">
import type { PublicShareFileModel, PublicSharePageModel } from '../../types'

defineProps<{
  page: PublicSharePageModel
  file: PublicShareFileModel
}>()
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
