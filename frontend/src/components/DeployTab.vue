<template>
  <v-card class="pa-5">
    <div class="d-flex align-center mb-4">
      <h3 class="text-h6 flex-grow-1">Env vars</h3>
      <v-btn size="small" prepend-icon="mdi-plus" @click="openNew = true">Add</v-btn>
    </div>
    <v-table density="compact" v-if="envs.length">
      <thead>
        <tr><th>Key</th><th>Value</th><th>Flags</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="e in envs" :key="e.key">
          <td><code>{{ e.key }}</code></td>
          <td>
            <code v-if="!e.isSecret">{{ e.value }}</code>
            <span v-else class="text-medium-emphasis">—</span>
          </td>
          <td>
            <v-chip v-if="e.isSecret" size="x-small" color="warning" variant="tonal">secret</v-chip>
            <v-chip v-if="e.exposeToFrontend" size="x-small" color="primary" variant="tonal" class="ml-1">PUBLIC_</v-chip>
          </td>
          <td class="text-right">
            <v-btn size="x-small" variant="text" icon="mdi-delete" @click="removeKey(e.key)" />
          </td>
        </tr>
      </tbody>
    </v-table>
    <div v-else class="text-medium-emphasis py-2">No env vars. Add any your app reads (PUBLIC_* for frontend, others stay secret).</div>

    <v-divider class="my-5" />

    <div class="d-flex align-center mb-3">
      <h3 class="text-h6 flex-grow-1">Deploy</h3>
      <v-btn
        color="primary"
        :disabled="!canDeploy"
        :loading="deploying"
        @click="onDeploy"
      >
        Deploy to {{ project.pierAppName }}
      </v-btn>
    </div>
    <v-alert v-if="!canDeploy" type="info" variant="tonal" density="compact" class="mb-3">
      Deploy is available after a successful build (DoneBuilding or DoneUpdating).
    </v-alert>
    <v-alert v-if="deployError" type="error" class="mb-3" closable @click:close="deployError = null">
      {{ deployError }} — see the latest deploy run's notes below for the full trace.
    </v-alert>

    <h4 class="text-subtitle-1 mt-5 mb-2">Deploy history</h4>
    <v-table density="compact" v-if="deploys.length">
      <thead>
        <tr><th>Status</th><th>Backend ver.</th><th>Frontend ver.</th><th>Started</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="d in deploys" :key="d.id">
          <td><v-chip size="x-small" :color="statusColor(d.status)" variant="tonal">{{ d.status }}</v-chip></td>
          <td>{{ d.pierDeployVersion ?? '—' }}</td>
          <td>{{ d.pierFrontendDeployVersion ?? '—' }}</td>
          <td>{{ new Date(d.startedAt).toLocaleString() }}</td>
          <td><v-btn v-if="d.deployNotes" size="x-small" variant="text" @click="notesRun = d">Notes</v-btn></td>
        </tr>
      </tbody>
    </v-table>
    <div v-else class="text-medium-emphasis">No deploys yet.</div>

    <!-- new env dialog -->
    <v-dialog v-model="openNew" max-width="480">
      <v-card class="pa-5">
        <h3 class="text-h6 mb-3">Add env var</h3>
        <v-text-field v-model="newVar.key" label="KEY" class="mb-3" />
        <v-text-field v-model="newVar.value" label="Value" class="mb-3" />
        <v-switch v-model="newVar.isSecret" color="warning" label="Secret (backend only, never exposed)" hide-details />
        <v-switch v-model="newVar.exposeToFrontend" color="primary" label="Expose to frontend (must start with PUBLIC_)" hide-details />
        <v-alert v-if="formError" type="error" density="compact" class="mt-3">{{ formError }}</v-alert>
        <div class="d-flex mt-4">
          <v-spacer />
          <v-btn variant="text" @click="openNew = false">Cancel</v-btn>
          <v-btn color="primary" :loading="savingNew" @click="saveNew">Save</v-btn>
        </div>
      </v-card>
    </v-dialog>

    <!-- notes dialog -->
    <v-dialog v-model="showNotes" max-width="720">
      <v-card class="pa-4">
        <h3 class="text-h6 mb-2">Deploy notes</h3>
        <pre class="notes">{{ notesRun?.deployNotes }}</pre>
      </v-card>
    </v-dialog>
  </v-card>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, type DeployRunDto, type EnvVarDto, type ProjectDto } from '../api/client'

const props = defineProps<{ project: ProjectDto }>()
const emit = defineEmits<{ (e: 'changed'): void }>()
const envs = ref<EnvVarDto[]>([])
const deploys = ref<DeployRunDto[]>([])
const deploying = ref(false)

const openNew = ref(false)
const newVar = ref({ key: '', value: '', isSecret: true, exposeToFrontend: false })
const formError = ref<string | null>(null)
const savingNew = ref(false)

const notesRun = ref<DeployRunDto | null>(null)
const showNotes = computed({ get: () => notesRun.value !== null, set: (v) => { if (!v) notesRun.value = null } })

const canDeploy = computed(() =>
  ['DoneBuilding', 'DoneUpdating'].includes(props.project.workspaceStatus),
)

async function reload() {
  envs.value    = await api.get<EnvVarDto[]>(`/api/projects/${props.project.id}/env`)
  deploys.value = await api.get<DeployRunDto[]>(`/api/projects/${props.project.id}/deploys`)
}
onMounted(reload)

watch(() => props.project.workspaceStatus, reload)

async function saveNew() {
  formError.value = null
  if (!newVar.value.key.match(/^[A-Z][A-Z0-9_]*$/)) {
    formError.value = 'Key should be UPPER_SNAKE_CASE.'
    return
  }
  if (newVar.value.exposeToFrontend && !newVar.value.key.startsWith('PUBLIC_')) {
    formError.value = 'Frontend-exposed keys must start with PUBLIC_.'
    return
  }
  savingNew.value = true
  try {
    await api.put(`/api/projects/${props.project.id}/env/${encodeURIComponent(newVar.value.key)}`, {
      value: newVar.value.value,
      isSecret: newVar.value.isSecret,
      exposeToFrontend: newVar.value.exposeToFrontend,
    })
    openNew.value = false
    newVar.value = { key: '', value: '', isSecret: true, exposeToFrontend: false }
    await reload()
  } catch (e: any) {
    formError.value = e?.body?.message ?? e?.body?.error ?? e?.message
  } finally {
    savingNew.value = false
  }
}

async function removeKey(key: string) {
  await api.delete(`/api/projects/${props.project.id}/env/${encodeURIComponent(key)}`)
  await reload()
}

const deployError = ref<string | null>(null)

async function onDeploy() {
  deploying.value = true
  deployError.value = null
  try {
    await api.post(`/api/projects/${props.project.id}/deploy`)
    await reload()
    emit('changed')
  } catch (e: any) {
    // Always refresh so the failed DeployRun shows up in history even
    // though the request itself errored.
    await reload().catch(() => {})
    emit('changed')
    const body = e?.body
    if (body && typeof body === 'object') {
      deployError.value = body.message ?? body.error ?? e.message ?? 'Deploy failed'
    } else {
      deployError.value = e?.message ?? 'Deploy failed'
    }
  } finally {
    deploying.value = false
  }
}

function statusColor(s: string) {
  if (s === 'succeeded') return 'success'
  if (s === 'failed') return 'error'
  if (s === 'running') return 'warning'
  return 'default'
}
</script>

<style scoped>
.notes { white-space: pre-wrap; font-family: ui-monospace, Menlo, monospace; font-size: 12px; max-height: 60vh; overflow-y: auto; padding: 12px; background: #0a0c0f; border-radius: 6px; }
</style>
