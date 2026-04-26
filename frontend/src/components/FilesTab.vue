<template>
  <v-card class="pa-0" flat>
    <div class="toolbar">
      <v-icon size="16" class="mr-2" color="primary">mdi-folder-open-outline</v-icon>
      <span class="toolbar-title">{{ project.pierAppName }}</span>
      <span class="toolbar-hint">{{ totalFiles }} file{{ totalFiles === 1 ? '' : 's' }}</span>
      <v-spacer />
      <v-btn
        size="small"
        variant="tonal"
        prepend-icon="mdi-refresh"
        :loading="loading"
        class="mr-2"
        @click="reload"
      >
        Refresh
      </v-btn>
      <v-btn
        size="small"
        color="primary"
        variant="tonal"
        prepend-icon="mdi-download"
        :disabled="totalFiles === 0"
        :href="zipUrl"
      >
        Download zip
      </v-btn>
    </div>
    <div class="split">
      <div class="tree">
        <v-progress-linear v-if="loading" indeterminate />
        <template v-else>
          <div v-if="rootNodes.length === 0" class="empty">Workspace is empty.</div>
          <div v-else class="tree-list">
            <FileTreeNode
              v-for="n in rootNodes"
              :key="n.path"
              :node="n"
              :depth="0"
              :selected="selected"
              :expanded-set="expandedSet"
              @select="open"
              @toggle="toggle"
            />
          </div>
        </template>
      </div>
      <div class="viewer">
        <div v-if="selected" class="viewer-header">
          <v-icon size="16" class="mr-2" color="primary">mdi-file-outline</v-icon>
          <span class="breadcrumb">{{ selected }}</span>
          <span v-if="fileBytes != null" class="bytes">{{ humanBytes(fileBytes) }}</span>
          <v-spacer />
          <v-btn
            size="x-small"
            variant="text"
            prepend-icon="mdi-content-copy"
            :disabled="!selected"
            @click="copyPath"
          >
            {{ copyPathLabel }}
          </v-btn>
          <v-btn
            size="x-small"
            variant="text"
            prepend-icon="mdi-clipboard-text-outline"
            :disabled="fileContent == null"
            @click="copyContent"
          >
            {{ copyContentLabel }}
          </v-btn>
        </div>
        <div v-if="viewError" class="pa-4">
          <v-alert type="warning" density="compact">
            <div>{{ viewError }}</div>
            <div v-if="viewErrorBytes != null" class="text-caption mt-1">
              File size: {{ humanBytes(viewErrorBytes) }}
            </div>
          </v-alert>
        </div>
        <div
          v-else-if="highlightedHtml"
          class="code shiki-host"
          v-html="highlightedHtml"
        />
        <pre v-else-if="fileContent !== null" class="code">{{ fileContent }}</pre>
        <div v-else class="placeholder">
          <v-icon size="48" color="primary" class="mb-3">mdi-file-tree-outline</v-icon>
          <div>Pick a file from the tree.</div>
        </div>
      </div>
    </div>
  </v-card>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api, type ProjectDto, type WorkspaceNodeDto } from '../api/client'
import { useConfigStore } from '../stores/config'
import FileTreeNode, { type TreeNode } from './FileTreeNode.vue'
import { highlight, languageForFile } from './highlight'

const props = defineProps<{ project: ProjectDto }>()

const nodes = ref<WorkspaceNodeDto[]>([])
const rootNodes = ref<TreeNode[]>([])
const loading = ref(true)
const selected = ref<string | null>(null)
const fileContent = ref<string | null>(null)
const fileBytes = ref<number | null>(null)
const viewError = ref<string | null>(null)
const viewErrorBytes = ref<number | null>(null)
const highlightedHtml = ref<string | null>(null)
const copyPathLabel = ref('Copy path')
const copyContentLabel = ref('Copy content')

const expandedSet = ref<Set<string>>(new Set(loadExpansion()))

const totalFiles = computed(() => nodes.value.filter((n) => !n.isDir).length)

const zipUrl = computed(() => {
  const cfg = useConfigStore()
  const base = cfg.apiBase && cfg.apiBase !== window.location.origin ? cfg.apiBase.replace(/\/$/, '') : ''
  return `${base}/api/projects/${props.project.id}/workspace/zip`
})

onMounted(reload)
watch(() => props.project.id, reload)

async function reload() {
  loading.value = true
  try {
    nodes.value = await api.get<WorkspaceNodeDto[]>(`/api/projects/${props.project.id}/workspace/tree`)
    rootNodes.value = buildTree(nodes.value)
    autoExpandTopLevel()
  } finally {
    loading.value = false
  }
}

function buildTree(flat: WorkspaceNodeDto[]): TreeNode[] {
  const byPath = new Map<string, TreeNode>()
  const roots: TreeNode[] = []
  for (const n of flat) {
    const t: TreeNode = { ...n, children: [] }
    byPath.set(n.path, t)
    const slash = n.path.lastIndexOf('/')
    if (slash === -1) {
      roots.push(t)
    } else {
      const parent = byPath.get(n.path.slice(0, slash))
      if (parent) parent.children.push(t)
      else roots.push(t)
    }
  }
  return roots
}

// On first load, expand top-level dirs (backend/ frontend/ etc.) so the
// tab isn't a wall of closed folders. User's saved state wins for
// subsequent visits.
function autoExpandTopLevel() {
  const saved = loadExpansion()
  if (saved.length > 0) {
    expandedSet.value = new Set(saved)
    return
  }
  const next = new Set<string>()
  for (const r of rootNodes.value) if (r.isDir) next.add(r.path)
  expandedSet.value = next
  saveExpansion()
}

function toggle(path: string) {
  const next = new Set(expandedSet.value)
  if (next.has(path)) next.delete(path)
  else next.add(path)
  expandedSet.value = next
  saveExpansion()
}

function storageKey() { return `fileTree:${props.project.id}` }
function loadExpansion(): string[] {
  try {
    const raw = sessionStorage.getItem(`fileTree:${props.project.id}`)
    return raw ? JSON.parse(raw) as string[] : []
  } catch { return [] }
}
function saveExpansion() {
  try { sessionStorage.setItem(storageKey(), JSON.stringify(Array.from(expandedSet.value))) }
  catch { /* quota exceeded is harmless */ }
}

async function open(path: string) {
  selected.value = path
  viewError.value = null
  viewErrorBytes.value = null
  fileContent.value = null
  fileBytes.value = null
  highlightedHtml.value = null
  try {
    const r = await api.get<{ path: string; content: string; bytes: number }>(
      `/api/projects/${props.project.id}/workspace/file?path=${encodeURIComponent(path)}`,
    )
    fileContent.value = r.content
    fileBytes.value = r.bytes
    const lang = languageForFile(basename(path))
    try {
      highlightedHtml.value = await highlight(r.content, lang)
    } catch {
      // Syntax highlighter is best-effort. Plain <pre> still shows content.
      highlightedHtml.value = null
    }
  } catch (e: unknown) {
    const err = e as { status?: number; body?: { error?: string; bytes?: number; cap?: number } }
    fileContent.value = null
    highlightedHtml.value = null
    const code = err.body?.error
    if (code === 'file-too-large') {
      viewError.value = `File is too large to preview (limit ${humanBytes(err.body?.cap ?? 0)}). Use "Download zip" to fetch it locally.`
      viewErrorBytes.value = err.body?.bytes ?? null
    } else if (code === 'binary-file') {
      viewError.value = 'Binary file — not previewable here.'
      viewErrorBytes.value = err.body?.bytes ?? null
    } else {
      viewError.value = code ?? (e instanceof Error ? e.message : 'Failed to load file')
    }
  }
}

function basename(p: string) {
  const i = p.lastIndexOf('/')
  return i === -1 ? p : p.slice(i + 1)
}

async function copyPath() {
  if (!selected.value) return
  try {
    await navigator.clipboard.writeText(selected.value)
    flashLabel(copyPathLabel, 'Copied!')
  } catch {
    flashLabel(copyPathLabel, 'Failed')
  }
}
async function copyContent() {
  if (fileContent.value == null) return
  try {
    await navigator.clipboard.writeText(fileContent.value)
    flashLabel(copyContentLabel, 'Copied!')
  } catch {
    flashLabel(copyContentLabel, 'Failed')
  }
}
function flashLabel(target: typeof copyPathLabel, text: string) {
  const original = target.value
  target.value = text
  setTimeout(() => { target.value = original }, 1200)
}

function humanBytes(b: number) {
  if (b < 1024) return `${b} B`
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`
  return `${(b / 1024 / 1024).toFixed(1)} MB`
}
</script>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  padding: 8px 12px;
  border: 1px solid rgba(255,255,255,0.05);
  border-radius: 8px 8px 0 0;
  border-bottom: 0;
  background: rgba(255,255,255,0.02);
  font-size: 13px;
}
.toolbar-title { font-family: ui-monospace, Menlo, monospace; font-weight: 500; }
.toolbar-hint { opacity: 0.5; margin-left: 8px; font-size: 12px; }

.split {
  display: grid;
  grid-template-columns: 320px 1fr;
  min-height: 62vh;
  border: 1px solid rgba(255,255,255,0.05);
  border-radius: 0 0 8px 8px;
  overflow: hidden;
}
.tree {
  border-right: 1px solid rgba(255,255,255,0.05);
  overflow-y: auto;
  max-height: 72vh;
  padding: 6px 0;
  background: rgba(0,0,0,0.08);
}
.tree-list { padding-bottom: 4px; }
.empty { padding: 16px; font-size: 13px; opacity: 0.6; }

.viewer { display: flex; flex-direction: column; max-height: 72vh; background: #0d1117; }
.viewer-header {
  display: flex;
  align-items: center;
  padding: 8px 14px;
  border-bottom: 1px solid rgba(255,255,255,0.05);
  font-size: 12px;
  background: #14171c;
  flex-shrink: 0;
}
.breadcrumb { font-family: ui-monospace, Menlo, monospace; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; opacity: 0.92; }
.bytes { margin-left: 12px; font-size: 11px; opacity: 0.5; font-variant-numeric: tabular-nums; }

.code { margin: 0; padding: 16px; font-family: ui-monospace, Menlo, Monaco, 'Courier New', monospace; font-size: 12px; line-height: 1.55; white-space: pre; overflow: auto; flex: 1 1 auto; }
.shiki-host :deep(pre) { margin: 0; padding: 16px; overflow: auto; background: transparent !important; }
.shiki-host :deep(code) { font-family: ui-monospace, Menlo, Monaco, 'Courier New', monospace; font-size: 12px; line-height: 1.55; }

.placeholder {
  padding: 64px 32px;
  text-align: center;
  opacity: 0.6;
  display: flex;
  flex-direction: column;
  align-items: center;
  flex: 1 1 auto;
  justify-content: center;
}
</style>
