<template>
  <div class="vcs-grid">
    <!-- Recovery card for imports whose initial clone failed. Only visible
         while isImported && the workspace has no .git yet. -->
    <v-card v-if="needsClone" class="pa-5 wide" color="surface-variant">
      <div class="d-flex align-center mb-3">
        <v-icon color="warning" class="mr-2">mdi-cloud-download-outline</v-icon>
        <h3 class="text-h6 flex-grow-1">Import — clone pending</h3>
        <v-btn
          color="primary"
          prepend-icon="mdi-download"
          :disabled="!state?.remoteUrl || !state?.branch"
          :loading="cloning"
          @click="onClone"
        >
          Clone from remote
        </v-btn>
      </div>
      <v-alert v-if="cloneResult && !cloneResult.ok" type="error" variant="tonal" density="compact" class="mb-2">
        Clone failed: {{ cloneResult.error }}
      </v-alert>
      <v-alert v-else-if="cloneResult?.ok" type="success" variant="tonal" density="compact" class="mb-2">
        Clone succeeded.
        <span v-if="cloneResult.envMirroredCount != null"> Mirrored {{ cloneResult.envMirroredCount }} env vars from Pier.</span>
        <span v-if="cloneResult.introspectionOk === false"> Introspection skipped.</span>
      </v-alert>
      <div class="text-body-2 text-medium-emphasis">
        This project is marked as imported but the workspace is empty — the initial clone never landed.
        Update the remote URL and branch above if needed, then click Clone from remote. The clone uses the
        AiBuilder host's SSH credentials.
      </div>
    </v-card>

    <v-card class="pa-5">
      <div class="d-flex align-center mb-3">
        <v-icon color="primary" class="mr-2">mdi-source-branch</v-icon>
        <h3 class="text-h6 flex-grow-1">Remote</h3>
        <v-btn
          v-if="state?.remoteUrl"
          size="small"
          variant="text"
          color="error"
          prepend-icon="mdi-link-off"
          :loading="unsetting"
          @click="onUnset"
        >
          Remove remote
        </v-btn>
      </div>
      <v-text-field
        v-model="form.remoteUrl"
        label="Remote URL"
        placeholder="git@github.com:owner/repo.git  or  https://github.com/owner/repo.git"
        prepend-inner-icon="mdi-link-variant"
        :hint="urlHint"
        persistent-hint
      />
      <v-text-field
        v-model="form.branch"
        label="Branch"
        placeholder="master"
        prepend-inner-icon="mdi-source-branch"
        class="mt-3"
      />
      <v-alert v-if="saveError" type="error" variant="tonal" density="compact" class="mt-3">
        {{ saveError }}
      </v-alert>
      <div class="d-flex mt-4">
        <v-spacer />
        <v-btn
          color="primary"
          :loading="saving"
          :disabled="!form.remoteUrl || !form.branch"
          @click="onSave"
        >
          Save remote
        </v-btn>
      </div>
    </v-card>

    <v-card class="pa-5">
      <div class="d-flex align-center mb-3">
        <v-icon color="primary" class="mr-2">mdi-git</v-icon>
        <h3 class="text-h6 flex-grow-1">Status</h3>
        <v-btn size="small" variant="text" icon="mdi-refresh" :loading="loadingState" @click="loadState" />
      </div>
      <div v-if="!state" class="text-medium-emphasis text-body-2">Loading…</div>
      <template v-else>
        <div class="stat-row">
          <span class="label">Current branch</span>
          <span class="value">
            <template v-if="state.currentBranch">
              <v-icon size="14" class="mr-1">mdi-source-branch</v-icon>{{ state.currentBranch }}
            </template>
            <span v-else class="muted">—</span>
          </span>
        </div>
        <div class="stat-row">
          <span class="label">HEAD</span>
          <span class="value">
            <template v-if="state.headShortSha">
              <code class="sha">{{ state.headShortSha }}</code>
              <span class="msg">{{ state.headSubject }}</span>
              <span v-if="state.headAt" class="muted small">· {{ timeAgo(state.headAt) }}</span>
            </template>
            <span v-else class="muted">No commits yet</span>
          </span>
        </div>
        <div class="stat-row">
          <span class="label">Last push</span>
          <span class="value">
            <template v-if="state.lastPushSha">
              <code class="sha">{{ state.lastPushSha.slice(0, 7) }}</code>
              <span v-if="state.lastPushAt" class="muted small">· {{ timeAgo(state.lastPushAt) }}</span>
              <v-chip v-if="pushedIsHead" size="x-small" color="success" variant="tonal" class="ml-2">up to date</v-chip>
              <v-chip v-else size="x-small" color="warning" variant="tonal" class="ml-2">commits unpushed</v-chip>
            </template>
            <span v-else class="muted">Never pushed</span>
          </span>
        </div>
      </template>
    </v-card>

    <v-card class="pa-5 wide">
      <div class="d-flex align-center mb-3">
        <v-icon color="primary" class="mr-2">mdi-download</v-icon>
        <h3 class="text-h6 flex-grow-1">Pull</h3>
        <v-btn
          color="primary"
          prepend-icon="mdi-download"
          :disabled="!canPull"
          :loading="pulling"
          @click="onPull(false)"
        >
          {{ pullButtonLabel }}
        </v-btn>
      </div>
      <v-alert v-if="!state?.remoteUrl" type="info" variant="tonal" density="compact" class="mb-3">
        Configure a remote URL above to enable pulling.
      </v-alert>
      <v-alert v-else-if="pullBusy" type="warning" variant="tonal" density="compact" class="mb-3">
        Project is {{ state ? '' : '' }}{{ pullBusyReason }} — pull is disabled until that finishes.
      </v-alert>
      <v-alert v-else type="info" variant="tonal" density="compact" class="mb-3">
        Fast-forwards the workspace to <code>origin/{{ state.branch ?? 'master' }}</code>. If there are local
        uncommitted changes the pull is refused unless you choose to discard them.
      </v-alert>

      <v-alert
        v-if="pullResult?.ok"
        type="success"
        variant="tonal"
        density="compact"
        class="mb-3"
      >
        <template v-if="pullResult.previousSha === pullResult.newSha">
          Already up to date with <code>origin/{{ state?.branch ?? 'master' }}</code>.
        </template>
        <template v-else>
          Pulled
          <code class="sha">{{ (pullResult.newSha ?? '').slice(0, 7) }}</code>
          ({{ pullResult.filesChanged }} file{{ pullResult.filesChanged === 1 ? '' : 's' }} changed).
        </template>
      </v-alert>

      <v-alert
        v-if="pullResult && !pullResult.ok && pullResult.errorCode === 'workspace-dirty'"
        type="warning"
        variant="tonal"
        density="compact"
        class="mb-3"
      >
        <div class="font-weight-medium mb-1">Workspace has uncommitted changes ({{ pullResult.uncommittedFiles.length }})</div>
        <ul class="dirty-list">
          <li v-for="f in pullResult.uncommittedFiles.slice(0, 12)" :key="f">{{ f }}</li>
          <li v-if="pullResult.uncommittedFiles.length > 12" class="muted">
            …and {{ pullResult.uncommittedFiles.length - 12 }} more
          </li>
        </ul>
        <div class="mt-3">
          <v-btn
            size="small"
            color="error"
            variant="flat"
            prepend-icon="mdi-alert-octagon"
            :loading="pulling"
            @click="confirmDiscardAndPull"
          >
            Discard local changes and pull
          </v-btn>
        </div>
      </v-alert>

      <v-alert
        v-if="pullResult && !pullResult.ok && pullResult.errorCode !== 'workspace-dirty'"
        type="error"
        variant="tonal"
        density="compact"
        class="mb-3"
      >
        <div class="font-weight-medium mb-1">Pull failed: {{ pullResult.errorCode }}</div>
        <div v-if="pullResult.errorMessage" class="text-body-2">{{ pullResult.errorMessage }}</div>
      </v-alert>

      <div v-if="pullResult?.output" class="log-frame">
        <pre class="log">{{ pullResult.output }}</pre>
      </div>

      <v-dialog v-model="discardDialog" max-width="520">
        <v-card class="pa-5">
          <h3 class="text-h6 mb-3">Discard local changes?</h3>
          <p class="text-body-2 mb-2">
            This hard-resets the workspace to <code>origin/{{ state?.branch ?? 'master' }}</code>
            and deletes untracked files. Any uncommitted edits in the workspace will be lost — including
            anything claude may have written during a build iteration.
          </p>
          <p class="text-body-2 mb-4">
            <strong>This cannot be undone.</strong>
          </p>
          <div class="d-flex">
            <v-spacer />
            <v-btn variant="text" @click="discardDialog = false">Cancel</v-btn>
            <v-btn
              color="error"
              variant="flat"
              class="ml-2"
              prepend-icon="mdi-alert-octagon"
              :loading="pulling"
              @click="performDiscardAndPull"
            >
              Discard and pull
            </v-btn>
          </div>
        </v-card>
      </v-dialog>
    </v-card>

    <v-card class="pa-5 wide">
      <div class="d-flex align-center mb-3">
        <v-icon color="primary" class="mr-2">mdi-upload</v-icon>
        <h3 class="text-h6 flex-grow-1">Push</h3>
        <v-btn
          color="primary"
          prepend-icon="mdi-upload"
          :disabled="!canPush"
          :loading="pushing"
          @click="onPush"
        >
          {{ pushButtonLabel }}
        </v-btn>
      </div>
      <v-alert v-if="!state?.remoteUrl" type="info" variant="tonal" density="compact" class="mb-3">
        Configure a remote URL above to enable pushing.
      </v-alert>
      <v-alert v-else type="warning" variant="tonal" density="compact" class="mb-3">
        The push uses the git credentials of the OS user running AiBuilder. Make sure that user has an SSH key registered
        with the remote (or a credential helper configured for HTTPS) — AiBuilder stores no tokens itself.
      </v-alert>
      <div class="log-frame">
        <pre ref="logEl" class="log">{{ logText || '— no push output yet —' }}</pre>
      </div>
    </v-card>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { api, type CloneResponse, type ProjectDto, type PullResponse, type VcsStateDto } from '../api/client'
import { useConfigStore } from '../stores/config'

const props = defineProps<{ project: ProjectDto }>()

const state = ref<VcsStateDto | null>(null)
const form = ref({ remoteUrl: '', branch: 'main' })
const saving = ref(false)
const unsetting = ref(false)
const saveError = ref<string | null>(null)
const loadingState = ref(false)

const logText = ref('')
const logEl = ref<HTMLPreElement>()
const pushing = ref(false)
const cloning = ref(false)
const cloneResult = ref<CloneResponse | null>(null)
const pulling = ref(false)
const pullResult = ref<PullResponse | null>(null)
const discardDialog = ref(false)
let eventSource: EventSource | null = null

const pullBusyReason = computed(() => {
  const s = props.project.workspaceStatus
  return s === 'Building' || s === 'Updating' ? s.toLowerCase() : ''
})
const pullBusy = computed(() => !!pullBusyReason.value)
const canPull = computed(() =>
  !!state.value?.remoteUrl && state.value?.workspaceHasGit && !pulling.value && !pullBusy.value)
const pullButtonLabel = computed(() => {
  const target = state.value?.remoteUrl
    ? `${shortenUrl(state.value.remoteUrl)} (${state.value.branch ?? 'master'})`
    : '—'
  return `Pull from ${target}`
})

const needsClone = computed(() =>
  !!state.value && state.value.isImported && !state.value.workspaceHasGit)

const urlHint = computed(() =>
  'SSH (git@host:path) or HTTPS URL. Do not embed credentials — AiBuilder rejects URLs with user:password.')

const canPush = computed(() => !!state.value?.remoteUrl && !pushing.value)
const pushButtonLabel = computed(() => {
  const target = state.value?.remoteUrl
    ? `${shortenUrl(state.value.remoteUrl)} (${state.value.branch ?? 'master'})`
    : '—'
  return `Push to ${target}`
})
const pushedIsHead = computed(() => {
  if (!state.value?.lastPushSha || !state.value?.headSha) return false
  return state.value.lastPushSha === state.value.headSha
})

function shortenUrl(u: string) {
  const m = /[:/]([^/:]+\/[^/:]+?)(?:\.git)?$/.exec(u)
  return m ? m[1] : u
}

onMounted(loadState)
onUnmounted(() => eventSource?.close())
watch(() => props.project.id, loadState)

async function loadState() {
  loadingState.value = true
  try {
    const s = await api.get<VcsStateDto>(`/api/projects/${props.project.id}/vcs`)
    state.value = s
    form.value.remoteUrl = s.remoteUrl ?? ''
    form.value.branch = s.branch ?? 'master'
  } finally {
    loadingState.value = false
  }
}

async function onSave() {
  saving.value = true
  saveError.value = null
  try {
    const s = await api.put<VcsStateDto>(`/api/projects/${props.project.id}/vcs`, {
      remoteUrl: form.value.remoteUrl.trim(),
      branch:    form.value.branch.trim() || 'master',
    })
    state.value = s
  } catch (e: unknown) {
    const err = e as { body?: { error?: string; reason?: string } }
    const reason = err?.body?.reason
    const code = err?.body?.error ?? 'save-failed'
    saveError.value = reason ? `${code}: ${reason}` : code
  } finally {
    saving.value = false
  }
}

async function onUnset() {
  unsetting.value = true
  try {
    await api.delete(`/api/projects/${props.project.id}/vcs`)
    form.value.remoteUrl = ''
    form.value.branch = 'master'
    await loadState()
  } finally {
    unsetting.value = false
  }
}

async function onClone() {
  cloning.value = true
  cloneResult.value = null
  try {
    const r = await api.post<CloneResponse>(`/api/projects/${props.project.id}/vcs/clone`)
    cloneResult.value = r
    await loadState()
  } catch (e: unknown) {
    const err = e as { body?: { error?: string; message?: string } }
    cloneResult.value = {
      ok: false,
      error: err?.body?.message ?? err?.body?.error ?? 'clone-failed',
      envMirroredCount: null,
      envMirrorError: null,
      introspectionOk: null,
      introspectionError: null,
    }
  } finally {
    cloning.value = false
  }
}

async function onPull(discardLocalChanges: boolean) {
  pulling.value = true
  if (!discardLocalChanges) pullResult.value = null
  try {
    const r = await api.post<PullResponse>(
      `/api/projects/${props.project.id}/vcs/pull`,
      { discardLocalChanges },
    )
    pullResult.value = r
    if (r.state) state.value = r.state
    else await loadState()
  } catch (e: unknown) {
    const err = e as { body?: PullResponse | { error?: string; message?: string } }
    const body = err?.body as Partial<PullResponse> | undefined
    if (body && typeof body.ok === 'boolean') {
      pullResult.value = body as PullResponse
      if (body.state) state.value = body.state
    } else {
      const generic = err?.body as { error?: string; message?: string } | undefined
      pullResult.value = {
        ok: false,
        errorCode: generic?.error ?? 'pull-failed',
        errorMessage: generic?.message ?? null,
        previousSha: null,
        newSha: null,
        filesChanged: 0,
        uncommittedFiles: [],
        output: '',
        state: null,
      }
    }
  } finally {
    pulling.value = false
    discardDialog.value = false
  }
}

function confirmDiscardAndPull() {
  discardDialog.value = true
}

async function performDiscardAndPull() {
  await onPull(true)
}

async function onPush() {
  pushing.value = true
  logText.value = ''
  closeStream()
  try {
    const r = await api.post<{ runId: string }>(`/api/projects/${props.project.id}/vcs/push`)
    attach(r.runId)
  } catch (e: unknown) {
    const err = e as { body?: { error?: string } }
    logText.value = `[aibuilder] push refused: ${err?.body?.error ?? 'unknown'}\n`
    pushing.value = false
  }
}

function closeStream() {
  eventSource?.close()
  eventSource = null
}

function attach(runId: string) {
  const cfg = useConfigStore()
  const base = cfg.apiBase && cfg.apiBase !== window.location.origin ? cfg.apiBase.replace(/\/$/, '') : ''
  eventSource = new EventSource(
    `${base}/api/projects/${props.project.id}/vcs/push/${runId}/stream`,
    { withCredentials: true },
  )
  eventSource.addEventListener('line', (ev) => {
    logText.value += (ev as MessageEvent).data + '\n'
    scrollLog()
  })
  eventSource.addEventListener('end', async () => {
    closeStream()
    pushing.value = false
    await loadState()
  })
  eventSource.onerror = () => {
    closeStream()
    pushing.value = false
  }
}

function scrollLog() {
  nextTick(() => { if (logEl.value) logEl.value.scrollTop = logEl.value.scrollHeight })
}

function timeAgo(iso: string) {
  const t = new Date(iso).getTime()
  if (Number.isNaN(t)) return iso
  const sec = Math.max(1, Math.round((Date.now() - t) / 1000))
  if (sec < 60) return `${sec}s ago`
  const min = Math.round(sec / 60)
  if (min < 60) return `${min}m ago`
  const hr = Math.round(min / 60)
  if (hr < 48) return `${hr}h ago`
  const day = Math.round(hr / 24)
  return `${day}d ago`
}
</script>

<style scoped>
.vcs-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}
.vcs-grid > .wide { grid-column: 1 / -1; }

.stat-row {
  display: flex;
  padding: 6px 0;
  border-bottom: 1px solid rgba(255,255,255,0.04);
  font-size: 13px;
}
.stat-row:last-child { border-bottom: 0; }
.stat-row .label { width: 140px; opacity: 0.55; font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px; padding-top: 2px; }
.stat-row .value { flex: 1; display: flex; align-items: center; flex-wrap: wrap; gap: 6px; }
.stat-row .msg { opacity: 0.85; }
.stat-row .muted { opacity: 0.4; }
.stat-row .muted.small { font-size: 11px; }
.stat-row .sha { font-family: ui-monospace, Menlo, monospace; background: rgba(106,168,255,0.1); color: #9ecbff; padding: 1px 6px; border-radius: 4px; font-size: 12px; }

.log-frame { border: 1px solid rgba(255,255,255,0.06); border-radius: 8px; background: #0a0c0f; }
.log { font-family: ui-monospace, Menlo, Monaco, 'Courier New', monospace; font-size: 12px; padding: 12px; white-space: pre-wrap; color: #c8cfd8; max-height: 360px; overflow-y: auto; margin: 0; }

.dirty-list { margin: 0; padding-left: 20px; font-family: ui-monospace, Menlo, Monaco, 'Courier New', monospace; font-size: 12px; }
.dirty-list li { margin: 2px 0; }
.dirty-list .muted { opacity: 0.6; font-style: italic; list-style: none; margin-left: -20px; }
</style>
