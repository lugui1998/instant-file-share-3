<script setup lang="ts">
defineProps<{
  label: string
  inputId?: string
}>()

const model = defineModel<boolean>({ required: true })
</script>

<template>
  <label class="toggle-row">
    <input :id="inputId" v-model="model" type="checkbox" />
    <span>{{ label }}</span>
  </label>
</template>
