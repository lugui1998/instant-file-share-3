<script setup lang="ts">
import { computed } from 'vue'
import type { TransferRecord } from '../../agentBridge'
import PanelHeader from '../PanelHeader.vue'

const props = defineProps<{
  transfers: TransferRecord[]
}>()

const transferTitle = computed(
  () => `${props.transfers.length} recent event${props.transfers.length === 1 ? '' : 's'}`,
)
</script>

<template>
  <section class="panel">
    <PanelHeader eyebrow="Transfers" :title="transferTitle" />

    <div class="table-shell">
      <table>
        <thead>
          <tr>
            <th>File</th>
            <th>Remote</th>
            <th>Bytes</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(transfer, index) in transfers" :key="index">
            <td>{{ transfer.fileName ?? 'Unknown file' }}</td>
            <td>{{ transfer.remoteAddress ?? 'n/a' }}</td>
            <td>{{ transfer.bytesSent ?? 0 }}</td>
            <td>{{ transfer.succeeded ? 'Completed' : 'Failed / partial' }}</td>
          </tr>
          <tr v-if="!transfers.length">
            <td class="empty-state" colspan="4">No transfer events yet.</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
