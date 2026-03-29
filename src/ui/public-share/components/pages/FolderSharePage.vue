<script setup lang="ts">
import type { PublicShareFolderModel, PublicSharePageModel } from '../../types'

defineProps<{
  page: PublicSharePageModel
  folder: PublicShareFolderModel
}>()
</script>

<template>
  <section class="stack">
    <nav
      v-if="folder.breadcrumbs.length > 1"
      class="breadcrumbs"
      aria-label="Folder path"
    >
      <a
        v-for="breadcrumb in folder.breadcrumbs"
        :key="breadcrumb.href"
        :href="breadcrumb.href"
      >
        {{ breadcrumb.label }}
      </a>
    </nav>

    <div v-if="folder.isEmpty" class="empty-state">
      <p>This folder is empty.</p>
    </div>

    <table v-else class="folder-table">
      <thead>
        <tr>
          <th>Name</th>
          <th>Modified</th>
          <th>Size</th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="entry in folder.entries"
          :key="entry.href"
        >
          <td>
            <a :href="entry.href">
              {{ entry.name }}<span v-if="entry.isDirectory && !entry.isParentDirectory">/</span>
            </a>
          </td>
          <td>{{ entry.modifiedAtLabel }}</td>
          <td>{{ entry.sizeLabel ?? '' }}</td>
        </tr>
      </tbody>
    </table>
  </section>
</template>
