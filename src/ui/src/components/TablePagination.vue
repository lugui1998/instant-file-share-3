<script setup lang="ts">
const props = defineProps<{
  currentPage: number
  pageCount: number
  totalItems: number
}>()

const emit = defineEmits<{
  updatePage: [page: number]
}>()

function goToPreviousPage() {
  if (props.currentPage > 1) {
    emit('updatePage', props.currentPage - 1)
  }
}

function goToNextPage() {
  if (props.currentPage < props.pageCount) {
    emit('updatePage', props.currentPage + 1)
  }
}
</script>

<template>
  <div v-if="totalItems > 0" class="pagination-controls">
    <span class="pagination-copy">Page {{ currentPage }} of {{ pageCount }} • {{ totalItems }} items</span>
    <div v-if="pageCount > 1" class="pagination-actions">
      <button class="secondary compact-button" type="button" :disabled="currentPage <= 1" @click="goToPreviousPage">
        Previous
      </button>
      <button class="secondary compact-button" type="button" :disabled="currentPage >= pageCount" @click="goToNextPage">
        Next
      </button>
    </div>
  </div>
</template>
