<script setup lang="ts">
defineProps<{
  eyebrow: string
  title: string
}>()
</script>

<template>
  <div class="panel-header">
    <div>
      <p class="eyebrow">{{ eyebrow }}</p>
      <h3>{{ title }}</h3>
    </div>

    <slot />
  </div>
</template>
