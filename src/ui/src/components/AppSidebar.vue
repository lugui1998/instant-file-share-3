<script setup lang="ts">
import { computed } from 'vue'
import type { ViewKey } from '../types/ui'

const props = defineProps<{
  activeView: ViewKey
  showLogs: boolean
}>()

const emit = defineEmits<{
  navigate: [view: ViewKey]
}>()

const navigationItems = computed(() => {
  const items: Array<{ key: ViewKey; label: string }> = [
    { key: 'shares', label: 'Shares' },
    { key: 'transfers', label: 'Transfers' },
    { key: 'settings', label: 'Settings' },
  ]

  if (props.showLogs) {
    items.push(
      { key: 'logs', label: 'Logs' },
      { key: 'cloudflareLogs', label: 'Cloudflare logs' },
    )
  }

  return items
})
</script>

<template>
  <aside class="rail">
    <div class="brand">
      <div class="brand-mark">IFS</div>
      <div>
        <p class="eyebrow">Instant File Share</p>
      </div>
    </div>

    <nav class="nav">
      <button
        v-for="item in navigationItems"
        :key="item.key"
        :aria-current="activeView === item.key ? 'page' : undefined"
        :class="{ active: activeView === item.key }"
        type="button"
        @click="emit('navigate', item.key)"
      >
        {{ item.label }}
      </button>
    </nav>
  </aside>
</template>
