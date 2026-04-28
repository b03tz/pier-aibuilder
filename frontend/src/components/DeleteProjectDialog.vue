<template>
  <v-dialog :model-value="modelValue" @update:model-value="$emit('update:modelValue', $event)" max-width="600" persistent>
    <v-card class="danger-card pa-5">
      <div class="d-flex align-center mb-3">
        <v-icon color="error" size="28" class="mr-3">mdi-alert-octagon</v-icon>
        <h3 class="text-h6 flex-grow-1">Delete {{ project.name }} forever?</h3>
      </div>

      <v-alert type="error" variant="tonal" density="compact" class="mb-3">
        This is permanent. AiBuilder cannot recover any of the records, files,
        or external resources you tick below — only the host's external
        services (Pier, Plexxer) might still have backups.
      </v-alert>

      <div class="text-body-2 mb-2"><strong>Always deleted:</strong></div>
      <ul class="text-body-2 mb-3 ml-4">
        <li>The AiBuilder project record (Pier + Plexxer creds, scope brief).</li>
        <li>Conversation turns, build runs, deploy runs, target env vars.</li>
        <li>The on-disk workspace (source, git history, build logs).</li>
      </ul>

      <div class="text-body-2 mb-2"><strong>Optionally:</strong></div>
      <v-checkbox
        v-model="deletePierApp"
        :disabled="!pierAdminConfigured"
        density="comfortable"
        hide-details
        class="mb-1"
      >
        <template #label>
          <span class="text-body-2">
            Delete the Pier app
            <code>{{ project.pierAppName }}</code>
            <span v-if="!pierAdminConfigured" class="text-caption text-medium-emphasis">
              (admin token not configured — set PIER_ADMIN_TOKEN to enable)
            </span>
          </span>
        </template>
      </v-checkbox>

      <v-checkbox
        v-model="deletePlexxerApp"
        :disabled="!hasPlexxer || !plexxerAccountConfigured"
        density="comfortable"
        hide-details
        class="mb-1"
      >
        <template #label>
          <span class="text-body-2">
            Delete the Plexxer app
            <code>{{ project.plexxerAppId || 'no plexxer configured' }}</code>
            <span v-if="hasPlexxer && !plexxerAccountConfigured" class="text-caption text-medium-emphasis">
              (account token not configured — set PLEXXER_ACCOUNT_TOKEN to enable)
            </span>
          </span>
        </template>
      </v-checkbox>

      <v-checkbox
        v-model="forcePlexxerError"
        :disabled="!deletePlexxerApp"
        density="comfortable"
        hide-details
        class="mb-2 ml-6"
      >
        <template #label>
          <span class="text-body-2 text-medium-emphasis">
            Force — continue if Plexxer can't delete the app
          </span>
        </template>
      </v-checkbox>

      <v-alert
        v-if="deletePlexxerApp"
        type="warning"
        variant="tonal"
        density="compact"
        class="mb-3"
      >
        This removes the entire Plexxer app — every entity, every row, and
        the app shell. Plexxer soft-deletes (recoverable for a short window
        via Plexxer's dashboard); after that it's gone.
        <span v-if="forcePlexxerError">
          <br /><strong>Force is on:</strong> if Plexxer rejects the call
          (already deleted manually, account token has no grants on this
          app, etc.) the rest of the cleanup proceeds anyway.
        </span>
      </v-alert>

      <v-divider class="mb-3" />

      <div class="text-body-2 mb-1">Type <code>{{ confirmPhrase }}</code> exactly to enable the button:</div>
      <v-text-field
        v-model="confirmInput"
        :placeholder="confirmPhrase"
        density="comfortable"
        spellcheck="false"
        autocomplete="off"
        prepend-inner-icon="mdi-keyboard-outline"
        class="mb-3 mono-input"
      />

      <v-alert v-if="error" type="error" density="compact" class="mb-3">
        {{ error }}
      </v-alert>

      <div class="d-flex">
        <v-spacer />
        <v-btn variant="text" :disabled="busy" @click="onCancel">Cancel</v-btn>
        <v-btn
          color="error"
          variant="flat"
          prepend-icon="mdi-delete-forever"
          :loading="busy"
          :disabled="!canSubmit"
          @click="onDelete"
        >
          Delete forever
        </v-btn>
      </div>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, type ProjectDto } from '../api/client'

const props = defineProps<{
  modelValue: boolean
  project: ProjectDto
  pierAdminConfigured: boolean
  plexxerAccountConfigured: boolean
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', v: boolean): void
  (e: 'deleted'): void
}>()

const deletePierApp     = ref(false)
const deletePlexxerApp  = ref(false)
const forcePlexxerError = ref(false)
const confirmInput      = ref('')
const error             = ref<string | null>(null)
const busy              = ref(false)

const hasPlexxer = computed(() => !!props.project.plexxerAppId)
const confirmPhrase = computed(() => `delete ${props.project.pierAppName}`)
const canSubmit = computed(() =>
  !busy.value && confirmInput.value === confirmPhrase.value)

// Reset transient state every time the dialog opens so the previous
// run's input doesn't sit there pre-typed.
watch(() => props.modelValue, (open) => {
  if (open) {
    deletePierApp.value     = props.pierAdminConfigured
    deletePlexxerApp.value  = hasPlexxer.value && props.plexxerAccountConfigured
    forcePlexxerError.value = false
    confirmInput.value      = ''
    error.value             = null
  }
})

// If the user unticks "Delete the Plexxer app" mid-flow, dragging the
// force flag along avoids it lingering as a no-op default for the next
// re-tick.
watch(deletePlexxerApp, (on) => { if (!on) forcePlexxerError.value = false })

function onCancel() {
  if (busy.value) return
  emit('update:modelValue', false)
}

async function onDelete() {
  busy.value = true
  error.value = null
  try {
    const params = new URLSearchParams({
      confirm:           confirmPhrase.value,
      deletePierApp:     String(deletePierApp.value),
      deletePlexxerApp:  String(deletePlexxerApp.value),
      forcePlexxerError: String(forcePlexxerError.value),
    })
    await api.delete(`/api/projects/${props.project.id}?${params.toString()}`)
    emit('deleted')
  } catch (e: any) {
    error.value = formatError(e)
  } finally {
    busy.value = false
  }
}

function formatError(e: any): string {
  const body = e?.body
  if (body && typeof body === 'object') {
    if (body.detail)  return `${body.title ?? body.error ?? 'error'}: ${body.detail}`
    if (body.message) return `${body.error ?? 'error'}: ${body.message}`
    if (body.error)   return body.error
    if (body.title)   return body.title
  }
  return e?.message ?? 'Delete failed'
}
</script>

<style scoped>
.danger-card {
  border: 1px solid rgba(244, 67, 54, 0.45);
  /* Layer the red wash over Vuetify's surface color — the previous
     `background:` shorthand wiped the surface, leaving the card
     transparent so the page bled through. */
  background-color: rgb(var(--v-theme-surface));
  background-image: linear-gradient(180deg, rgba(244, 67, 54, 0.06) 0%, transparent 60%);
}
.mono-input :deep(input) {
  font-family: ui-monospace, Menlo, Monaco, 'Courier New', monospace;
  font-size: 13px;
}
code {
  font-family: ui-monospace, Menlo, Monaco, 'Courier New', monospace;
  background: rgba(244, 67, 54, 0.12);
  color: #ff8a80;
  padding: 1px 6px;
  border-radius: 4px;
  font-size: 12px;
}
</style>
