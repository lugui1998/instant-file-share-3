<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { agentBridge } from '../agentBridge'
import { REPOSITORY_URL } from '../../shared/repository'
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
    { key: 'transfers', label: 'History' },
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

const appVersion = ref('')

async function loadAppVersion() {
  try {
    appVersion.value = await agentBridge.getAppVersion()
  } catch {
    appVersion.value = ''
  }
}

onMounted(() => {
  void loadAppVersion()
})
</script>

<template>
  <aside class="rail">
    <div class="brand">
      <a class="eyebrow brand-link" :href="REPOSITORY_URL" target="_blank" rel="noreferrer">Instant File Share</a>
      <p v-if="appVersion" class="version-label">v{{ appVersion }}</p>
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
