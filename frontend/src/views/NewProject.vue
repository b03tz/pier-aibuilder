<template>
  <v-container>
    <h1 class="text-h5 mb-4">{{ headline }}</h1>
    <v-card class="pa-6" max-width="720">
      <v-btn-toggle
        v-model="form.isImport"
        mandatory
        color="primary"
        density="compact"
        class="mb-5"
      >
        <v-btn :value="false">New project</v-btn>
        <v-btn :value="true">Import existing</v-btn>
      </v-btn-toggle>

      <v-form @submit.prevent="onSubmit">
        <v-text-field v-model="form.name" label="Project name *" class="mb-3" />

        <!-- Auto-create-on-Pier panel.
             Visible only on new projects; disabled with a setup hint
             when PIER_ADMIN_TOKEN isn't configured. -->
        <v-card
          v-if="!form.isImport"
          variant="outlined"
          class="pa-4 mb-3"
          :class="autoCreateAllowed ? 'border-primary-tone' : 'border-muted'"
        >
          <div class="d-flex align-center">
            <v-checkbox
              v-model="form.autoCreateOnPier"
              :disabled="!autoCreateAllowed"
              hide-details
              density="compact"
              class="mr-3"
            />
            <div class="flex-grow-1">
              <div class="text-subtitle-2">Create project on Pier</div>
              <div class="text-caption text-medium-emphasis">
                <template v-if="autoCreateAllowed">
                  Pier will host this app and receive deploys automatically.
                  Uncheck to bind an existing Pier app instead.
                </template>
                <template v-else>
                  Set <code>PIER_ADMIN_TOKEN</code> and
                  <code>PIER_ADMIN_BASE</code> in Pier env to enable
                  automatic Pier provisioning.
                  <router-link to="/settings">Open Settings →</router-link>
                </template>
              </div>
            </div>
          </div>

          <!-- Live preview + manual override + has-frontend toggle -->
          <template v-if="autoCreate">
            <v-divider class="my-3" />
            <div class="text-caption text-medium-emphasis mb-1">
              Pier subdomain (auto-derived from the project name)
            </div>
            <v-text-field
              v-model="form.slugOverride"
              :placeholder="slugPreview || 'will be filled in once you type a name'"
              label="Subdomain"
              density="comfortable"
              :error-messages="slugOverrideError ?? undefined"
              hide-details="auto"
              prepend-inner-icon="mdi-link-variant"
            >
              <template v-if="!form.slugOverride && slugPreview" #append-inner>
                <span class="text-caption text-medium-emphasis">{{ slugPreview }}.onpier.tech</span>
              </template>
            </v-text-field>
            <div class="text-caption text-medium-emphasis mt-1">
              Leave blank to use the auto-derived value. Must match
              <code>^[a-z][a-z0-9-]{1,30}$</code>.
            </div>

            <v-checkbox
              v-model="form.hasFrontend"
              label="Includes a frontend (Vue UI)"
              hint="Uncheck for a backend-only API app."
              persistent-hint
              density="compact"
              class="mt-3"
            />
          </template>
        </v-card>

        <!-- Manual Pier credentials (shown when not auto-creating, or for imports) -->
        <template v-if="needsManualPierFields">
          <v-text-field
            v-model="form.pierAppName"
            label="Pier app name (subdomain) *"
            :hint="form.isImport
              ? 'The existing Pier subdomain — must match ^[a-z][a-z0-9-]{1,30}$'
              : 'Must match ^[a-z][a-z0-9-]{1,30}$'"
            persistent-hint
            class="mb-3"
          />
          <v-text-field
            v-model="form.pierApiToken"
            :label="form.isImport ? 'Existing Pier API token *' : 'Pier API token *'"
            type="password"
            class="mb-3"
          />
        </template>

        <v-divider class="my-4" />
        <div class="text-caption text-medium-emphasis mb-2">
          <span v-if="form.isImport">
            Plexxer (optional — leave both empty if the existing app doesn't use Plexxer).
          </span>
          <span v-else>
            Plexxer (optional — leave both fields empty for apps that don't need persistence, e.g. pure frontend apps).
          </span>
        </div>
        <v-text-field
          v-model="form.plexxerAppId"
          :label="form.isImport ? 'Existing Plexxer app ID' : 'Plexxer app ID'"
          class="mb-3"
        />
        <v-text-field
          v-model="form.plexxerApiToken"
          :label="form.isImport ? 'Existing Plexxer API token' : 'Plexxer API token'"
          type="password"
          class="mb-3"
        />

        <v-divider class="my-4" />
        <div class="text-caption text-medium-emphasis mb-2">
          <span v-if="form.isImport">
            Git remote — required. The repo will be cloned into the workspace at create time.
            SSH only. Auth comes from the OS user's SSH key on the AiBuilder host.
          </span>
          <span v-else>
            Git remote (optional) — set this if you want to push your workspace to a remote
            from the Version Control tab later. SSH only.
          </span>
        </div>
        <v-text-field
          v-model="form.gitRemoteUrl"
          :label="form.isImport ? 'Git remote URL *' : 'Git remote URL'"
          placeholder="git@github.com:owner/repo.git"
          class="mb-3"
        />
        <v-text-field
          v-model="form.gitRemoteBranch"
          label="Branch"
          hint="Defaults to master"
          class="mb-3"
        />

        <v-divider class="my-4" />
        <v-textarea
          v-model="form.scopeBrief"
          :label="form.isImport ? 'What are we adding or changing? *' : 'Scope brief *'"
          :hint="form.isImport
            ? 'Describe the change you want to make to the existing codebase.'
            : 'What should this app do? One-liner is fine — refine in the scope conversation.'"
          rows="4"
          class="mb-4"
        />
        <v-alert v-if="error" type="error" class="mb-3">{{ error }}</v-alert>
        <div class="d-flex">
          <v-btn to="/" variant="text">Cancel</v-btn>
          <v-spacer />
          <v-btn color="primary" type="submit" :loading="busy" :disabled="!canSubmit">
            {{ submitLabel }}
          </v-btn>
        </div>
      </v-form>
    </v-card>
  </v-container>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, type CreateProjectResponse, type PierAdminStatusDto } from '../api/client'
import { derivePierAppSlug, isValidPierAppSlug } from '../pierAppSlug'

const form = reactive({
  name: '',
  pierAppName: '',
  pierApiToken: '',
  plexxerAppId: '',
  plexxerApiToken: '',
  scopeBrief: '',
  gitRemoteUrl: '',
  gitRemoteBranch: 'master',
  isImport: false as boolean,
  autoCreateOnPier: true as boolean,
  hasFrontend: true as boolean,
  slugOverride: '',
})

const adminStatus = ref<PierAdminStatusDto | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)
const router = useRouter()

onMounted(async () => {
  try {
    adminStatus.value = await api.get<PierAdminStatusDto>('/api/_pier-admin/status')
  } catch {
    // Endpoint requires auth; if we're not logged in we'll have been
    // redirected away. Treat as "not configured" so the UI doesn't lie.
    adminStatus.value = { configured: false, base: '', tokenLastFour: null }
  }
})

const autoCreateAllowed = computed(() =>
  !form.isImport && (adminStatus.value?.configured ?? false))

const autoCreate = computed(() =>
  autoCreateAllowed.value && form.autoCreateOnPier)

const needsManualPierFields = computed(() => form.isImport || !autoCreate.value)

const slugPreview = computed(() => {
  if (!form.name.trim()) return ''
  return derivePierAppSlug(form.name)
})

const slugOverrideError = computed(() => {
  if (!form.slugOverride) return null
  return isValidPierAppSlug(form.slugOverride.trim())
    ? null
    : 'Must match ^[a-z][a-z0-9-]{1,30}$'
})

const headline = computed(() => form.isImport ? 'Import existing project' : 'New project')

const submitLabel = computed(() => {
  if (form.isImport) return 'Import'
  if (autoCreate.value) return 'Create on Pier'
  return 'Create'
})

const canSubmit = computed(() => {
  if (busy.value) return false
  if (!form.name.trim() || !form.scopeBrief.trim()) return false
  if (autoCreate.value) {
    // Override slug, when present, must be valid.
    return slugOverrideError.value === null
  }
  // Manual / import path — pier creds are required.
  return form.pierAppName.trim().length >= 2 && form.pierApiToken.length > 0
})

async function onSubmit() {
  busy.value = true
  error.value = null
  try {
    const usingAutoCreate = autoCreate.value
    const body: Record<string, unknown> = {
      name:            form.name,
      scopeBrief:      form.scopeBrief,
      isImport:        form.isImport,
      plexxerAppId:    form.plexxerAppId.trim()    || undefined,
      plexxerApiToken: form.plexxerApiToken.trim() || undefined,
      gitRemoteUrl:    form.gitRemoteUrl.trim()    || undefined,
      gitRemoteBranch: form.gitRemoteBranch.trim() || undefined,
    }
    if (usingAutoCreate) {
      body.autoCreateOnPier = true
      body.hasFrontend      = form.hasFrontend
      // Override is optional; only include when the user typed one.
      if (form.slugOverride.trim()) body.pierAppName = form.slugOverride.trim()
    } else {
      body.autoCreateOnPier = false
      body.pierAppName  = form.pierAppName.trim()
      body.pierApiToken = form.pierApiToken
    }
    const r = await api.post<CreateProjectResponse>('/api/projects', body)
    router.push({ name: 'project', params: { id: r.project.id } })
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
    if (body.reason)  return `${body.error ?? 'error'}: ${body.reason}`
    if (body.error)   return body.error
    if (body.title)   return body.title
  }
  return e?.message ?? 'Create failed'
}
</script>

<style scoped>
.border-primary-tone { border-color: rgba(106, 168, 255, 0.32); }
.border-muted        { border-color: rgba(255, 255, 255, 0.06); }
</style>
