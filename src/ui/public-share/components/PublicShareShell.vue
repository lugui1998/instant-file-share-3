<script setup lang="ts">
import { computed } from 'vue'
import type { PublicSharePageModel } from '../types'

const props = defineProps<{
  page: PublicSharePageModel
}>()

const descriptionSegments = computed(() => {
  const siteName = props.page.siteName
  if (!siteName) {
    return [{ text: props.page.description, isRepositoryReference: false }]
  }

  const parts = props.page.description.split(siteName)
  return parts.flatMap((part, index) => {
    const segments = [{ text: part, isRepositoryReference: false }]
    if (index < parts.length - 1) {
      segments.push({ text: siteName, isRepositoryReference: true })
    }

    return segments
  }).filter((part) => part.text.length > 0)
})
</script>

<template>
  <div
    class="public-shell"
    :class="{
      'public-shell--folder': page.kind === 'folder',
      'public-shell--receive': page.kind === 'receive',
    }"
  >
    <main class="public-shell__card">
      <header class="public-shell__header">
        <div>
          <a class="public-shell__eyebrow public-shell__brand-link" :href="page.repositoryUrl" target="_blank" rel="noreferrer">
            {{ page.siteName }}
          </a>
          <h1 class="public-shell__title">{{ page.title }}</h1>
          <p
            v-if="page.kind !== 'folder' && page.kind !== 'receive'"
            class="public-shell__description"
          >
            <template v-for="(segment, index) in descriptionSegments" :key="`${index}-${segment.text}`">
              <a
                v-if="segment.isRepositoryReference"
                class="public-shell__inline-link"
                :href="page.repositoryUrl"
                target="_blank"
                rel="noreferrer"
              >
                {{ segment.text }}
              </a>
              <template v-else>{{ segment.text }}</template>
            </template>
          </p>
        </div>

        <slot name="header-meta">
          <a
            v-if="page.primaryActionLabel && page.primaryActionUrl"
            class="public-shell__action"
            :href="page.primaryActionUrl"
          >
            {{ page.primaryActionLabel }}
          </a>
        </slot>
      </header>

      <slot />
    </main>
  </div>
</template>
