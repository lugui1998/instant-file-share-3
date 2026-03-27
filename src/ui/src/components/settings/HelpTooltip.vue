<script setup lang="ts">
defineProps<{
  text: string
}>()
</script>

<template>
  <span
    class="help-tooltip"
    :title="text"
    aria-label="More information"
    tabindex="0"
    role="note"
  >
    ?
  </span>
</template>
