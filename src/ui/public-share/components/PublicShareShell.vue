<script setup lang="ts">
import type { PublicSharePageModel } from '../types'

defineProps<{
  page: PublicSharePageModel
}>()
</script>

<template>
  <div
    class="public-shell"
    :class="{ 'public-shell--folder': page.kind === 'folder' }"
  >
    <main class="public-shell__card">
      <header class="public-shell__header">
        <div>
          <p class="public-shell__eyebrow">{{ page.siteName }}</p>
          <h1 class="public-shell__title">{{ page.title }}</h1>
          <p
            v-if="page.kind !== 'folder'"
            class="public-shell__description"
          >
            {{ page.description }}
          </p>
        </div>

        <a
          v-if="page.primaryActionLabel && page.primaryActionUrl"
          class="public-shell__action"
          :href="page.primaryActionUrl"
        >
          {{ page.primaryActionLabel }}
        </a>
      </header>

      <slot />
    </main>
  </div>
</template>
