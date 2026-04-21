<template>
  <v-container>
    <div class="d-flex align-center mb-4">
      <h1 class="text-h5">Projects</h1>
      <v-spacer />
      <v-btn color="primary" prepend-icon="mdi-plus" :to="{ name: 'new-project' }">New project</v-btn>
    </div>
    <v-progress-linear v-if="loading" indeterminate />
    <v-alert v-else-if="error" type="error" class="mb-3">{{ error }}</v-alert>
    <template v-else>
      <v-card v-if="projects.length === 0" class="pa-6 text-center text-medium-emphasis">
        No projects yet. Click <strong>New project</strong> to create one.
      </v-card>
      <v-list v-else border rounded="lg">
        <v-list-item
          v-for="p in projects"
          :key="p.id"
          :to="{ name: 'project', params: { id: p.id } }"
        >
          <div class="d-flex align-center">
            <div class="flex-grow-1">
              <div class="text-subtitle-1">{{ p.name }}</div>
              <div class="text-caption text-medium-emphasis">{{ p.pierAppName }}.onpier.tech · {{ p.workspaceStatus }}</div>
            </div>
            <v-chip size="small" :color="statusColor(p.workspaceStatus)" variant="tonal">{{ p.workspaceStatus }}</v-chip>
          </div>
        </v-list-item>
      </v-list>
    </template>
  </v-container>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, type ProjectDto } from '../api/client'

const projects = ref<ProjectDto[]>([])
const loading = ref(true)
const error = ref<string | null>(null)

onMounted(async () => {
  try { projects.value = await api.get<ProjectDto[]>('/api/projects') }
  catch (e: any) { error.value = e?.message ?? 'Failed to load projects' }
  finally { loading.value = false }
})

function statusColor(s: string) {
  if (s.startsWith('Done')) return 'success'
  if (s === 'Building' || s === 'Updating') return 'warning'
  if (s === 'Deployed') return 'primary'
  return 'default'
}
</script>
