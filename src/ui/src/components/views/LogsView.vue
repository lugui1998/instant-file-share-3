<script setup lang="ts">
import PanelHeader from '../PanelHeader.vue'

defineProps<{
  eyebrow: string
  title: string
  logs: string
}>()

const emit = defineEmits<{
  refresh: []
}>()
</script>

<template>
  <section class="panel">
    <PanelHeader :eyebrow="eyebrow" :title="title">
      <button class="secondary compact-button" type="button" @click="emit('refresh')">Refresh</button>
    </PanelHeader>

    <pre>{{ logs || 'No logs yet.' }}</pre>
  </section>
</template>
