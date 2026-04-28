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
        <v-btn
          :href="pierManageUrl"
          target="_blank"
          rel="noopener"
          variant="text"
          size="small"
          prepend-icon="mdi-open-in-new"
          class="mr-1"
        >
          Manage in Pier
        </v-btn>
        <v-btn
          v-if="plexxerManageUrl"
          :href="plexxerManageUrl"
          target="_blank"
          rel="noopener"
          variant="text"
          size="small"
          prepend-icon="mdi-open-in-new"
          class="mr-1"
        >
          Manage in Plexxer
        </v-btn>
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
            <v-list-item
              prepend-icon="mdi-delete-forever"
              title="Delete project…"
              subtitle="Forever — Pier app + AiBuilder record + (optionally) Plexxer schemas"
              base-color="error"
              @click="openDelete"
            />
          </v-list>
        </v-menu>
      </div>

      <DeleteProjectDialog
        v-if="project"
        v-model="showDelete"
        :project="project"
        :pier-admin-configured="pierAdminConfigured"
        :plexxer-account-configured="plexxerAccountConfigured"
        @deleted="onDeleted"
      />

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
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, type PierAdminStatusDto, type PlexxerAdminStatusDto, type ProjectDto } from '../api/client'
import ScopeTab from '../components/ScopeTab.vue'
import BuildTab from '../components/BuildTab.vue'
import FilesTab from '../components/FilesTab.vue'
import VersionControlTab from '../components/VersionControlTab.vue'
import DeployTab from '../components/DeployTab.vue'
import DeleteProjectDialog from '../components/DeleteProjectDialog.vue'

const props = defineProps<{ id: string }>()
const router = useRouter()
const project = ref<ProjectDto | null>(null)
const loading = ref(true)
const tab = ref('scope')

const showReset = ref(false)
const confirmInput = ref('')
const resetting = ref(false)
const resetError = ref<string | null>(null)

const showDelete = ref(false)
const pierAdminConfigured     = ref(false)
const plexxerAccountConfigured = ref(false)

async function openDelete() {
  // Pull both admin-statuses fresh so the dialog renders the right
  // default-state for each "Delete the … app" checkbox.
  const [p, x] = await Promise.allSettled([
    api.get<PierAdminStatusDto>('/api/_pier-admin/status'),
    api.get<PlexxerAdminStatusDto>('/api/_plexxer-admin/status'),
  ])
  pierAdminConfigured.value      = p.status === 'fulfilled' ? p.value.configured : false
  plexxerAccountConfigured.value = x.status === 'fulfilled' ? x.value.configured : false
  showDelete.value = true
}

function onDeleted() {
  showDelete.value = false
  router.push({ name: 'projects' })
}

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

// Direct deep-link into Pier's admin UI for the project's app. Works for
// both auto-created and manually-bound projects since the URL is a
// function of the pierAppName alone.
const pierManageUrl = computed(() =>
  project.value
    ? `https://admin.onpier.tech/Apps/Detail/${encodeURIComponent(project.value.pierAppName)}`
    : '#')

// Direct deep-link into Plexxer's dashboard for the project's app.
// Hidden when the project doesn't use Plexxer.
const plexxerManageUrl = computed(() =>
  project.value?.plexxerAppId
    ? `https://plexxer.com/apps/${encodeURIComponent(project.value.plexxerAppId)}`
    : null)

function statusColor(s: string) {
  if (s.startsWith('Done')) return 'success'
  if (s === 'Building' || s === 'Updating') return 'warning'
  if (s === 'Deployed') return 'primary'
  return 'default'
}
</script>
