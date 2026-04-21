<template>
  <v-card class="pa-5">
    <p class="text-body-2 text-medium-emphasis mb-4">
      Initial brief: {{ project.scopeBrief }}
    </p>

    <!-- Composer at top so the input is always on screen without scrolling. -->
    <template v-if="canTalk">
      <v-textarea v-model="draft" label="Your message" rows="3" class="mb-3" />
      <div class="d-flex mb-5">
        <v-spacer />
        <v-btn
          v-if="canLock"
          variant="outlined"
          class="mr-2"
          :loading="locking"
          @click="onLock"
        >
          Lock scope
        </v-btn>
        <v-btn color="primary" :disabled="!draft.trim()" :loading="sending" @click="onSend">
          Send
        </v-btn>
      </div>
    </template>
    <div v-else class="mb-5">
      <v-alert type="info" variant="tonal" density="compact" class="mb-3">
        Scope is <strong>{{ project.workspaceStatus }}</strong>.
        <template v-if="canUnlock"> Unlock to add more turns.</template>
      </v-alert>
      <div v-if="canUnlock" class="d-flex">
        <v-spacer />
        <v-btn variant="outlined" color="warning" :loading="unlocking" @click="onUnlock">
          Unlock scope
        </v-btn>
      </div>
    </div>

    <!-- Transcript below, newest first so the turn you just sent appears
         right under the composer without scrolling. -->
    <div class="transcript">
      <div v-if="turns.length === 0" class="text-medium-emphasis text-center py-4">
        No turns yet. Start the conversation above.
      </div>
      <div v-for="t in reversedTurns" :key="t.id" :class="['turn', `turn-${t.role}`]">
        <div class="role">{{ t.role }}</div>
        <div class="content">{{ t.content }}</div>
      </div>
    </div>
  </v-card>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, type ProjectDto, type TurnDto } from '../api/client'

const props = defineProps<{ project: ProjectDto }>()
const emit = defineEmits<{ (e: 'changed'): void }>()
const turns = ref<TurnDto[]>([])
const draft = ref('')
const sending = ref(false)
const locking = ref(false)
const unlocking = ref(false)

const canTalk = computed(() =>
  ['Draft', 'InConversation', 'Deployed'].includes(props.project.workspaceStatus),
)
const canLock = computed(() =>
  props.project.workspaceStatus === 'InConversation' && turns.value.length > 0,
)
// Unlock is only useful from a "closed" but non-live state. Building and
// Updating are in-flight — admin should cancel those, not unlock them.
// Deployed auto-flips to InConversation when you send a turn, so the
// explicit button there would be redundant.
const canUnlock = computed(() =>
  ['ScopeLocked', 'DoneBuilding', 'DoneUpdating'].includes(props.project.workspaceStatus),
)
const reversedTurns = computed(() => [...turns.value].sort((a, b) => b.turnIndex - a.turnIndex))

async function load() {
  turns.value = await api.get<TurnDto[]>(`/api/projects/${props.project.id}/turns`)
}
onMounted(load)

async function onSend() {
  sending.value = true
  try {
    const r = await api.post<{ user: TurnDto; assistant: TurnDto }>(
      `/api/projects/${props.project.id}/turns`,
      { message: draft.value },
    )
    turns.value.push(r.user, r.assistant)
    draft.value = ''
    emit('changed')
  } finally {
    sending.value = false
  }
}

async function onLock() {
  locking.value = true
  try {
    await api.post(`/api/projects/${props.project.id}/lock-scope`)
    emit('changed')
  } finally {
    locking.value = false
  }
}

async function onUnlock() {
  unlocking.value = true
  try {
    await api.post(`/api/projects/${props.project.id}/unlock-scope`)
    emit('changed')
  } finally {
    unlocking.value = false
  }
}
</script>

<style scoped>
.transcript { display: flex; flex-direction: column; gap: 12px; max-height: 60vh; overflow-y: auto; }
.turn { padding: 12px 14px; border-radius: 8px; border: 1px solid rgba(255,255,255,0.06); }
.turn-user { background: rgba(106,168,255,0.08); }
.turn-assistant { background: rgba(196,181,253,0.06); }
.role { font-size: 11px; text-transform: uppercase; opacity: 0.6; letter-spacing: 1px; margin-bottom: 4px; }
.content { white-space: pre-wrap; font-size: 14px; line-height: 1.5; }
</style>
