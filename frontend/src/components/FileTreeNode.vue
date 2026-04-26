<template>
  <div class="node-row" :class="{ dir: node.isDir, active: !node.isDir && selected === node.path }">
    <button
      type="button"
      class="node-btn"
      :style="{ paddingLeft: depth * 14 + 8 + 'px' }"
      @click="onClick"
    >
      <span class="chevron">
        <template v-if="node.isDir">
          <v-icon size="14">{{ expanded ? 'mdi-chevron-down' : 'mdi-chevron-right' }}</v-icon>
        </template>
      </span>
      <v-icon size="16" class="icon" :color="iconColor">{{ iconName }}</v-icon>
      <span class="name">{{ node.name }}</span>
      <span v-if="node.size != null" class="size">{{ human(node.size) }}</span>
    </button>
    <div v-if="node.isDir && expanded" class="children">
      <FileTreeNode
        v-for="c in node.children"
        :key="c.path"
        :node="c"
        :depth="depth + 1"
        :selected="selected"
        :expanded-set="expandedSet"
        @select="$emit('select', $event)"
        @toggle="$emit('toggle', $event)"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'

export interface TreeNode {
  path: string
  name: string
  isDir: boolean
  size: number | null
  children: TreeNode[]
}

defineOptions({ name: 'FileTreeNode' })

const props = defineProps<{
  node: TreeNode
  depth: number
  selected: string | null
  expandedSet: Set<string>
}>()
const emit = defineEmits<{
  (e: 'select', path: string): void
  (e: 'toggle', path: string): void
}>()

const expanded = computed(() => props.expandedSet.has(props.node.path))

function onClick() {
  if (props.node.isDir) emit('toggle', props.node.path)
  else emit('select', props.node.path)
}

const iconName = computed(() => {
  if (props.node.isDir) return expanded.value ? 'mdi-folder-open' : 'mdi-folder'
  return iconForFile(props.node.name)
})
const iconColor = computed(() => (props.node.isDir ? 'primary' : undefined))

function iconForFile(name: string): string {
  const lower = name.toLowerCase()
  if (lower === 'dockerfile') return 'mdi-docker'
  if (lower === '.gitignore' || lower === '.gitattributes') return 'mdi-git'
  if (lower === 'readme.md') return 'mdi-book-open-variant'
  if (lower === 'package.json' || lower === 'package-lock.json') return 'mdi-nodejs'
  const dot = lower.lastIndexOf('.')
  const ext = dot >= 0 ? lower.slice(dot + 1) : ''
  switch (ext) {
    case 'cs':    return 'mdi-language-csharp'
    case 'csproj':
    case 'slnx':
    case 'sln':   return 'mdi-dot-net'
    case 'vue':   return 'mdi-vuejs'
    case 'ts':
    case 'tsx':   return 'mdi-language-typescript'
    case 'js':
    case 'jsx':
    case 'mjs':
    case 'cjs':   return 'mdi-language-javascript'
    case 'json':  return 'mdi-code-json'
    case 'yml':
    case 'yaml':  return 'mdi-file-code-outline'
    case 'md':    return 'mdi-language-markdown'
    case 'html':  return 'mdi-language-html5'
    case 'css':
    case 'scss':
    case 'sass':  return 'mdi-language-css3'
    case 'sh':
    case 'bash':  return 'mdi-console'
    case 'py':    return 'mdi-language-python'
    case 'go':    return 'mdi-language-go'
    case 'rs':    return 'mdi-language-rust'
    case 'sql':   return 'mdi-database'
    case 'png':
    case 'jpg':
    case 'jpeg':
    case 'gif':
    case 'svg':
    case 'webp':
    case 'ico':   return 'mdi-file-image-outline'
    case 'lock':  return 'mdi-lock-outline'
    case 'env':   return 'mdi-key-variant'
    case 'txt':
    case 'log':   return 'mdi-file-document-outline'
    default:      return 'mdi-file-outline'
  }
}

function human(b: number) {
  if (b < 1024) return `${b} B`
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`
  return `${(b / 1024 / 1024).toFixed(1)} MB`
}
</script>

<style scoped>
.node-btn {
  display: flex;
  align-items: center;
  width: 100%;
  padding: 4px 12px 4px 0;
  background: transparent;
  border: 0;
  color: inherit;
  cursor: pointer;
  font-size: 13px;
  text-align: left;
  border-radius: 4px;
  line-height: 1.3;
}
.node-btn:hover { background: rgba(255,255,255,0.04); }
.active > .node-btn { background: rgba(106,168,255,0.14); color: #cfe0ff; }
.chevron { width: 18px; display: inline-flex; justify-content: center; opacity: 0.7; }
.icon { margin-right: 6px; }
.name { flex: 1; font-family: ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.dir > .node-btn .name { font-weight: 500; }
.size { font-size: 10px; opacity: 0.45; margin-left: 8px; font-variant-numeric: tabular-nums; }
</style>
