<script setup lang="ts">
defineProps<{
  eyebrow: string
}>()
</script>

<template>
  <article class="setting-card">
    <p class="eyebrow">{{ eyebrow }}</p>
    <slot />
  </article>
</template>
