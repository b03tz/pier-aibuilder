<template>
  <v-container>
    <v-progress-linear v-if="loading" indeterminate />
    <template v-else-if="project">
      <div class="d-flex align-center mb-4">
        <div class="flex-grow-1">
          <h1 class="text-h5">{{ project.name }}</h1>
          <div class="text-caption text-medium-emphasis">
            {{ project.pierAppName }}.onpier.tech · Plexxer {{ project.plexxerAppId }}
          </div>
        </div>
        <v-chip :color="statusColor(project.workspaceStatus)" variant="tonal">
          {{ project.workspaceStatus }}
        </v-chip>
      </div>

      <v-tabs v-model="tab" class="mb-4">
        <v-tab value="scope">Scope</v-tab>
        <v-tab value="build">Build</v-tab>
        <v-tab value="files">Files</v-tab>
        <v-tab value="deploy">Deploy</v-tab>
      </v-tabs>

      <v-window v-model="tab">
        <v-window-item value="scope"><ScopeTab :project="project" @changed="reload" /></v-window-item>
        <v-window-item value="build"><BuildTab :project="project" @changed="reload" /></v-window-item>
        <v-window-item value="files"><FilesTab :project="project" /></v-window-item>
        <v-window-item value="deploy"><DeployTab :project="project" @changed="reload" /></v-window-item>
      </v-window>
    </template>
  </v-container>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, type ProjectDto } from '../api/client'
import ScopeTab from '../components/ScopeTab.vue'
import BuildTab from '../components/BuildTab.vue'
import FilesTab from '../components/FilesTab.vue'
import DeployTab from '../components/DeployTab.vue'

const props = defineProps<{ id: string }>()
const project = ref<ProjectDto | null>(null)
const loading = ref(true)
const tab = ref('scope')

async function reload() {
  project.value = await api.get<ProjectDto>(`/api/projects/${props.id}`)
}
onMounted(async () => {
  try { await reload() }
  finally { loading.value = false }
})

function statusColor(s: string) {
  if (s.startsWith('Done')) return 'success'
  if (s === 'Building' || s === 'Updating') return 'warning'
  if (s === 'Deployed') return 'primary'
  return 'default'
}
</script>
