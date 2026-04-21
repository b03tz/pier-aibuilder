<template>
  <v-card class="pa-5">
    <div class="d-flex align-center mb-4">
      <h3 class="text-h6 flex-grow-1">Builds</h3>
      <v-btn
        v-if="runningRunId"
        color="error"
        variant="outlined"
        class="mr-2"
        :loading="cancelling"
        @click="onCancel"
      >
        Cancel
      </v-btn>
      <v-btn
        color="primary"
        :disabled="!canBuild"
        :loading="starting"
        @click="onBuild"
      >
        {{ isIteration ? 'Rebuild' : 'Build' }}
      </v-btn>
    </div>
    <v-alert v-if="!canBuild" type="info" variant="tonal" density="compact" class="mb-3">
      Lock the scope first to start a build.
    </v-alert>

    <div class="log-frame mb-4">
      <pre ref="logEl" class="log">{{ logText || '— no output yet —' }}</pre>
    </div>

    <h4 class="text-subtitle-1 mb-2">History</h4>
    <v-table density="compact" v-if="runs.length">
      <thead>
        <tr><th>Kind</th><th>Status</th><th>Started</th><th>Finished</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="r in runs" :key="r.id">
          <td>{{ r.kind }}</td>
          <td><v-chip size="x-small" :color="statusColor(r.status)" variant="tonal">{{ r.status }}</v-chip></td>
          <td>{{ new Date(r.startedAt).toLocaleString() }}</td>
          <td>{{ r.finishedAt ? new Date(r.finishedAt).toLocaleString() : '—' }}</td>
          <td><v-btn size="x-small" variant="text" @click="attach(r.id)">View</v-btn></td>
        </tr>
      </tbody>
    </v-table>
    <div v-else class="text-medium-emphasis">No runs yet.</div>
  </v-card>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import { api, type BuildRunDto, type ProjectDto } from '../api/client'
import { useConfigStore } from '../stores/config'

const props = defineProps<{ project: ProjectDto }>()
const emit = defineEmits<{ (e: 'changed'): void }>()

const runs = ref<BuildRunDto[]>([])
const logText = ref('')
const logEl = ref<HTMLPreElement>()
const starting = ref(false)
const cancelling = ref(false)
let eventSource: EventSource | null = null

const canBuild = computed(() => props.project.workspaceStatus === 'ScopeLocked')
const isIteration = computed(() => runs.value.some((r) => r.status === 'succeeded'))
const runningRunId = computed(() => runs.value.find((r) => r.status === 'running')?.id ?? null)

async function loadRuns() {
  runs.value = await api.get<BuildRunDto[]>(`/api/projects/${props.project.id}/builds`)
}
onMounted(async () => {
  await loadRuns()
  const live = runs.value.find((r) => r.status === 'running')
  if (live) attach(live.id)
})
onUnmounted(() => eventSource?.close())

function closeStream() {
  eventSource?.close()
  eventSource = null
}

function attach(runId: string) {
  closeStream()
  logText.value = ''
  const cfg = useConfigStore()
  const base = cfg.apiBase && cfg.apiBase !== window.location.origin ? cfg.apiBase.replace(/\/$/, '') : ''
  eventSource = new EventSource(`${base}/api/projects/${props.project.id}/builds/${runId}/stream`, { withCredentials: true })
  eventSource.addEventListener('line', (ev) => {
    logText.value += (ev as MessageEvent).data + '\n'
    scrollLog()
  })
  eventSource.addEventListener('end', async () => {
    closeStream()
    await loadRuns()
    emit('changed')
  })
  eventSource.onerror = () => closeStream()
}

async function onCancel() {
  const rid = runningRunId.value
  if (!rid) return
  cancelling.value = true
  try {
    await api.post(`/api/projects/${props.project.id}/builds/${rid}/cancel`)
    // The SSE stream will emit the [aibuilder] build cancelled line shortly;
    // the run-list refresh happens in the stream's `end` handler.
  } finally {
    cancelling.value = false
  }
}

async function onBuild() {
  starting.value = true
  try {
    const r = await api.post<{ runId: string }>(`/api/projects/${props.project.id}/build`)
    await loadRuns()
    attach(r.runId)
    emit('changed')
  } finally {
    starting.value = false
  }
}

function scrollLog() {
  nextTick(() => { if (logEl.value) logEl.value.scrollTop = logEl.value.scrollHeight })
}

function statusColor(s: string) {
  if (s === 'succeeded') return 'success'
  if (s === 'failed') return 'error'
  if (s === 'running') return 'warning'
  return 'default'
}
</script>

<style scoped>
.log-frame { border: 1px solid rgba(255,255,255,0.06); border-radius: 8px; background: #0a0c0f; }
.log { font-family: ui-monospace, Menlo, Monaco, 'Courier New', monospace; font-size: 12px; padding: 12px; white-space: pre-wrap; color: #c8cfd8; max-height: 420px; overflow-y: auto; margin: 0; }
</style>
