<template>
  <v-card class="pa-0" flat>
    <div class="split">
      <div class="tree">
        <div class="tree-header">Workspace</div>
        <v-progress-linear v-if="loading" indeterminate />
        <div v-else>
          <div v-if="nodes.length === 0" class="empty">Workspace is empty.</div>
          <button
            v-for="n in nodes"
            :key="n.path"
            type="button"
            class="node"
            :class="{ dir: n.isDir, active: selected === n.path }"
            @click="!n.isDir && open(n.path)"
          >
            <span class="icon">{{ n.isDir ? '📁' : '📄' }}</span>
            <span class="path">{{ n.path }}</span>
            <span v-if="n.size != null" class="size">{{ human(n.size) }}</span>
          </button>
        </div>
      </div>
      <div class="viewer">
        <div v-if="viewError" class="pa-4">
          <v-alert type="warning" density="compact">{{ viewError }}</v-alert>
        </div>
        <pre v-else-if="fileContent !== null" class="code">{{ fileContent }}</pre>
        <div v-else class="placeholder">Pick a file from the tree.</div>
      </div>
    </div>
  </v-card>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, type ProjectDto, type WorkspaceNodeDto } from '../api/client'

const props = defineProps<{ project: ProjectDto }>()
const nodes = ref<WorkspaceNodeDto[]>([])
const loading = ref(true)
const selected = ref<string | null>(null)
const fileContent = ref<string | null>(null)
const viewError = ref<string | null>(null)

onMounted(async () => {
  try { nodes.value = await api.get<WorkspaceNodeDto[]>(`/api/projects/${props.project.id}/workspace/tree`) }
  finally { loading.value = false }
})

async function open(path: string) {
  selected.value = path
  viewError.value = null
  try {
    const r = await api.get<{ path: string; content: string; bytes: number }>(
      `/api/projects/${props.project.id}/workspace/file?path=${encodeURIComponent(path)}`,
    )
    fileContent.value = r.content
  } catch (e: any) {
    fileContent.value = null
    viewError.value = e?.body?.error ?? e?.message ?? 'Failed to load file'
  }
}

function human(b: number) {
  if (b < 1024) return `${b} B`
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`
  return `${(b / 1024 / 1024).toFixed(1)} MB`
}
</script>

<style scoped>
.split { display: grid; grid-template-columns: 320px 1fr; min-height: 60vh; border: 1px solid rgba(255,255,255,0.05); border-radius: 8px; overflow: hidden; }
.tree { border-right: 1px solid rgba(255,255,255,0.05); overflow-y: auto; max-height: 70vh; }
.tree-header { padding: 10px 14px; font-size: 11px; text-transform: uppercase; letter-spacing: 1px; opacity: 0.65; border-bottom: 1px solid rgba(255,255,255,0.05); }
.node { display: flex; align-items: center; width: 100%; padding: 6px 12px; background: transparent; border: 0; color: inherit; cursor: pointer; font-size: 13px; text-align: left; }
.node:hover { background: rgba(255,255,255,0.03); }
.node.active { background: rgba(106,168,255,0.12); }
.node.dir { cursor: default; opacity: 0.72; }
.node .icon { margin-right: 8px; }
.node .path { flex: 1; font-family: ui-monospace, Menlo, monospace; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.node .size { font-size: 11px; opacity: 0.5; margin-left: 8px; }
.viewer { overflow: auto; max-height: 70vh; }
.code { margin: 0; padding: 16px; font-family: ui-monospace, Menlo, monospace; font-size: 12px; white-space: pre; }
.placeholder { padding: 32px; text-align: center; opacity: 0.5; }
.empty { padding: 16px; font-size: 13px; opacity: 0.6; }
</style>
