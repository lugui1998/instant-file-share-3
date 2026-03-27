<script setup lang="ts">
import HelpTooltip from './HelpTooltip.vue'

defineProps<{
  label: string
  inputId?: string
  helpText?: string
}>()

const model = defineModel<boolean>({ required: true })
</script>

<template>
  <label class="toggle-row">
    <input :id="inputId" v-model="model" type="checkbox" />
    <span>{{ label }}</span>
    <HelpTooltip v-if="helpText" :text="helpText" />
  </label>
</template>
