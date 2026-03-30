<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'

const props = withDefaults(defineProps<{
  confirmLabel?: string
  cancelLabel?: string
  message: string
  pending?: boolean
  title: string
}>(), {
  confirmLabel: 'Confirm',
  cancelLabel: 'Cancel',
  pending: false,
})

const emit = defineEmits<{
  cancel: []
  confirm: []
}>()

function handleKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape' && !props.pending) {
    emit('cancel')
  }
}

onMounted(() => {
  window.addEventListener('keydown', handleKeyDown)
})

onUnmounted(() => {
  window.removeEventListener('keydown', handleKeyDown)
})
</script>

<template>
  <div class="modal-backdrop" @click.self="emit('cancel')">
    <section class="modal-card" aria-modal="true" role="dialog" :aria-busy="pending">
      <p class="eyebrow">Confirm</p>
      <h3>{{ title }}</h3>
      <p class="modal-copy">{{ message }}</p>

      <div class="card-actions modal-actions">
        <button class="secondary" type="button" :disabled="pending" @click="emit('cancel')">
          {{ cancelLabel }}
        </button>
        <button class="danger" type="button" :disabled="pending" @click="emit('confirm')">
          {{ pending ? 'Clearing...' : confirmLabel }}
        </button>
      </div>
    </section>
  </div>
</template>
