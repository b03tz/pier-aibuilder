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
        <v-chip :color="statusColor(project.workspaceStatus)" variant="tonal" class="mr-2">
          {{ project.workspaceStatus }}
        </v-chip>
        <v-menu>
          <template #activator="{ props: menuProps }">
            <v-btn v-bind="menuProps" icon="mdi-dots-vertical" size="small" variant="text" />
          </template>
          <v-list density="compact">
            <v-list-item
              prepend-icon="mdi-refresh-circle"
              title="Reset project…"
              subtitle="Wipe history, turns, runs, env, files"
              @click="showReset = true"
            />
          </v-list>
        </v-menu>
      </div>

      <v-dialog v-model="showReset" max-width="520">
        <v-card class="pa-5">
          <h3 class="text-h6 mb-2">Reset {{ project.name }}?</h3>
          <p class="text-body-2 mb-3">
            This will permanently delete all conversation turns, build runs, deploy runs,
            target env vars, and the entire on-disk workspace (source + git history + logs).
            The project record itself (name, Pier + Plexxer creds, scope brief) stays.
          </p>
          <p class="text-body-2 mb-3 text-medium-emphasis">
            The app currently running on <code>{{ project.pierAppName }}.onpier.tech</code> is
            <strong>not</strong> touched — redeploy an empty build if you want to clear the
            live site.
          </p>
          <v-text-field
            v-model="confirmInput"
            :label="`Type '${project.pierAppName}' to confirm`"
            density="comfortable"
            class="mb-3"
          />
          <v-alert v-if="resetError" type="error" density="compact" class="mb-3">
            {{ resetError }}
          </v-alert>
          <div class="d-flex">
            <v-spacer />
            <v-btn variant="text" @click="showReset = false">Cancel</v-btn>
            <v-btn
              color="error"
              :loading="resetting"
              :disabled="confirmInput !== project.pierAppName"
              @click="onReset"
            >
              Reset
            </v-btn>
          </div>
        </v-card>
      </v-dialog>

      <v-tabs v-model="tab" class="mb-4">
        <v-tab value="scope">Scope</v-tab>
        <v-tab value="build">Build</v-tab>
        <v-tab value="files">Files</v-tab>
        <v-tab value="vcs">Version Control</v-tab>
        <v-tab value="deploy">Deploy</v-tab>
      </v-tabs>

      <v-window v-model="tab">
        <v-window-item value="scope"><ScopeTab :project="project" @changed="reload" /></v-window-item>
        <v-window-item value="build"><BuildTab :project="project" @changed="reload" /></v-window-item>
        <v-window-item value="files"><FilesTab :project="project" /></v-window-item>
        <v-window-item value="vcs"><VersionControlTab :project="project" /></v-window-item>
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
import VersionControlTab from '../components/VersionControlTab.vue'
import DeployTab from '../components/DeployTab.vue'

const props = defineProps<{ id: string }>()
const project = ref<ProjectDto | null>(null)
const loading = ref(true)
const tab = ref('scope')

const showReset = ref(false)
const confirmInput = ref('')
const resetting = ref(false)
const resetError = ref<string | null>(null)

async function reload() {
  project.value = await api.get<ProjectDto>(`/api/projects/${props.id}`)
}
onMounted(async () => {
  try { await reload() }
  finally { loading.value = false }
})

async function onReset() {
  if (!project.value) return
  resetting.value = true
  resetError.value = null
  try {
    await api.post(`/api/projects/${props.id}/reset?confirm=${encodeURIComponent(project.value.pierAppName)}`)
    await reload()
    tab.value = 'scope'
    showReset.value = false
    confirmInput.value = ''
  } catch (e: any) {
    resetError.value = e?.body?.message ?? e?.body?.error ?? e?.message ?? 'Reset failed'
  } finally {
    resetting.value = false
  }
}

function statusColor(s: string) {
  if (s.startsWith('Done')) return 'success'
  if (s === 'Building' || s === 'Updating') return 'warning'
  if (s === 'Deployed') return 'primary'
  return 'default'
}
</script>
