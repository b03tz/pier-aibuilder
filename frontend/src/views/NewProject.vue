<template>
  <v-container>
    <h1 class="text-h5 mb-4">{{ headline }}</h1>
    <v-card class="pa-6" max-width="900">
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
        <v-text-field v-model="form.name" label="Project name *" class="mb-1" />
        <div v-if="!form.isImport && slugPreview" class="text-caption text-medium-emphasis mb-3">
          Subdomain: <code>{{ slugPreview }}</code>
          <span class="text-medium-emphasis">— used as the default name on both providers</span>
        </div>
        <div v-else class="mb-3" />

        <!-- Two-card provisioning row. Hidden in import mode (imports always
             attach to existing infrastructure on both sides). -->
        <v-row v-if="!form.isImport" dense class="mb-3">
          <v-col cols="12" md="6">
            <ProvisionCard
              title="Pier hosting"
              icon="mdi-server"
              providerEnvVar="PIER_ADMIN_TOKEN"
              :status="pierStatus"
              :mode="form.pierMode"
              @update:mode="form.pierMode = $event"
              :slug-preview="slugPreview"
            >
              <template #auto>
                <v-text-field
                  v-model="form.pierSlugOverride"
                  :placeholder="slugPreview || 'will be filled in once you type a name'"
                  label="Subdomain override (optional)"
                  density="comfortable"
                  :error-messages="pierSlugOverrideError ?? undefined"
                  hide-details="auto"
                  prepend-inner-icon="mdi-link-variant"
                  class="mb-2"
                />
                <div class="text-caption text-medium-emphasis mb-2">
                  Final URL: <code>{{ pierEffectiveSlug || '…' }}.onpier.tech</code>
                </div>
                <v-checkbox
                  v-model="form.hasFrontend"
                  label="Includes a frontend (Vue UI)"
                  hide-details
                  density="compact"
                />
              </template>
              <template #existing>
                <v-text-field
                  v-model="form.pierAppName"
                  label="Pier app name (subdomain) *"
                  hint="Must match ^[a-z][a-z0-9-]{1,30}$"
                  persistent-hint
                  density="comfortable"
                  class="mb-2"
                />
                <v-text-field
                  v-model="form.pierApiToken"
                  label="Pier API token *"
                  type="password"
                  density="comfortable"
                />
              </template>
            </ProvisionCard>
          </v-col>

          <v-col cols="12" md="6">
            <ProvisionCard
              title="Plexxer database"
              icon="mdi-database"
              providerEnvVar="PLEXXER_ACCOUNT_TOKEN"
              :status="plexxerStatus"
              :mode="form.plexxerMode"
              @update:mode="form.plexxerMode = $event"
              :slug-preview="slugPreview"
              allow-none
            >
              <template #auto>
                <div class="text-caption text-medium-emphasis">
                  A fresh Plexxer app will be created and AiBuilder will
                  mint a per-app token automatically. The app name on
                  Plexxer's dashboard defaults to <code>{{ slugPreview || '…' }}</code>;
                  the unique <code>appKey</code> is server-assigned.
                </div>
              </template>
              <template #existing>
                <v-text-field
                  v-model="form.plexxerAppId"
                  label="Plexxer app ID *"
                  density="comfortable"
                  class="mb-2"
                />
                <v-text-field
                  v-model="form.plexxerApiToken"
                  label="Plexxer API token *"
                  type="password"
                  density="comfortable"
                />
              </template>
              <template #empty>
                <div class="text-caption text-medium-emphasis">
                  This project won't use a Plexxer database. Pure
                  frontend / stateless apps only.
                </div>
              </template>
            </ProvisionCard>
          </v-col>
        </v-row>

        <!-- Import-mode credentials. Both Pier + Plexxer creds may be
             needed since we're attaching to existing infrastructure. -->
        <template v-if="form.isImport">
          <v-divider class="my-4" />
          <div class="text-caption text-medium-emphasis mb-2">
            Pier (existing app — required)
          </div>
          <v-text-field
            v-model="form.pierAppName"
            label="Pier app name (subdomain) *"
            hint="The existing Pier subdomain — must match ^[a-z][a-z0-9-]{1,30}$"
            persistent-hint
            class="mb-3"
          />
          <v-text-field
            v-model="form.pierApiToken"
            label="Existing Pier API token *"
            type="password"
            class="mb-3"
          />
          <div class="text-caption text-medium-emphasis mb-2">
            Plexxer (optional — leave both empty if the existing app doesn't use Plexxer)
          </div>
          <v-text-field
            v-model="form.plexxerAppId"
            label="Existing Plexxer app ID"
            class="mb-3"
          />
          <v-text-field
            v-model="form.plexxerApiToken"
            label="Existing Plexxer API token"
            type="password"
            class="mb-3"
          />
        </template>

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
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, type CreateProjectResponse, type PierAdminStatusDto, type PlexxerAdminStatusDto } from '../api/client'
import { derivePierAppSlug, isValidPierAppSlug } from '../pierAppSlug'
import ProvisionCard from '../components/ProvisionCard.vue'

type ProvisionMode = 'auto' | 'existing' | 'none'

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
  hasFrontend: true as boolean,
  pierMode:    'auto'     as ProvisionMode,
  plexxerMode: 'auto'     as ProvisionMode,
  pierSlugOverride: '',
})

const pierStatus    = ref<PierAdminStatusDto    | null>(null)
const plexxerStatus = ref<PlexxerAdminStatusDto | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)
const router = useRouter()

onMounted(async () => {
  // Two parallel status reads. Failure to fetch either is treated as
  // "not configured" so the UI degrades to the manual paste flow rather
  // than silently lying.
  const [p, x] = await Promise.allSettled([
    api.get<PierAdminStatusDto>('/api/_pier-admin/status'),
    api.get<PlexxerAdminStatusDto>('/api/_plexxer-admin/status'),
  ])
  pierStatus.value    = p.status === 'fulfilled' ? p.value : { configured: false, base: '', tokenLastFour: null }
  plexxerStatus.value = x.status === 'fulfilled' ? x.value : { configured: false, base: '', tokenLastFour: null }

  // If a side isn't configured, demote its mode to "existing" so the
  // form doesn't try to submit an auto-create the backend will refuse.
  if (!pierStatus.value.configured && form.pierMode === 'auto')       form.pierMode = 'existing'
  if (!plexxerStatus.value.configured && form.plexxerMode === 'auto') form.plexxerMode = 'existing'
})

const slugPreview = computed(() => {
  if (!form.name.trim()) return ''
  return derivePierAppSlug(form.name)
})

const pierEffectiveSlug = computed(() => {
  return form.pierSlugOverride.trim() || slugPreview.value
})

const pierSlugOverrideError = computed(() => {
  if (!form.pierSlugOverride.trim()) return null
  return isValidPierAppSlug(form.pierSlugOverride.trim())
    ? null
    : 'Must match ^[a-z][a-z0-9-]{1,30}$'
})

const headline = computed(() => form.isImport ? 'Import existing project' : 'New project')

const submitLabel = computed(() => {
  if (form.isImport) return 'Import'
  if (form.pierMode === 'auto' && form.plexxerMode === 'auto') return 'Create on Pier + Plexxer'
  if (form.pierMode === 'auto')    return 'Create on Pier'
  if (form.plexxerMode === 'auto') return 'Create on Plexxer'
  return 'Create'
})

const canSubmit = computed(() => {
  if (busy.value) return false
  if (!form.name.trim() || !form.scopeBrief.trim()) return false

  if (form.isImport) {
    if (form.pierAppName.trim().length < 2) return false
    if (form.pierApiToken.length === 0) return false
    if (!form.gitRemoteUrl.trim()) return false
    return true
  }

  // New project: each provider's selection must be self-consistent.
  if (form.pierMode === 'auto' && pierSlugOverrideError.value !== null) return false
  if (form.pierMode === 'existing') {
    if (form.pierAppName.trim().length < 2) return false
    if (form.pierApiToken.length === 0) return false
  }
  if (form.plexxerMode === 'existing') {
    if (!form.plexxerAppId.trim() || !form.plexxerApiToken.trim()) return false
  }
  return true
})

// Clear paste-fields when switching back to "auto", so we don't
// accidentally submit stale values.
watch(() => form.pierMode, (m) => {
  if (m === 'auto') {
    form.pierAppName = ''
    form.pierApiToken = ''
  }
})
watch(() => form.plexxerMode, (m) => {
  if (m === 'auto' || m === 'none') {
    form.plexxerAppId = ''
    form.plexxerApiToken = ''
  }
})

async function onSubmit() {
  busy.value = true
  error.value = null
  try {
    const body: Record<string, unknown> = {
      name:            form.name,
      scopeBrief:      form.scopeBrief,
      isImport:        form.isImport,
      gitRemoteUrl:    form.gitRemoteUrl.trim()    || undefined,
      gitRemoteBranch: form.gitRemoteBranch.trim() || undefined,
    }

    if (form.isImport) {
      // Imports always attach to existing infra on both sides.
      body.autoCreateOnPier    = false
      body.autoCreateOnPlexxer = false
      body.pierAppName         = form.pierAppName.trim()
      body.pierApiToken        = form.pierApiToken
      body.plexxerAppId        = form.plexxerAppId.trim()    || undefined
      body.plexxerApiToken     = form.plexxerApiToken.trim() || undefined
    } else {
      body.autoCreateOnPier    = form.pierMode === 'auto'
      body.autoCreateOnPlexxer = form.plexxerMode === 'auto'

      if (form.pierMode === 'auto') {
        body.hasFrontend = form.hasFrontend
        if (form.pierSlugOverride.trim()) body.pierAppName = form.pierSlugOverride.trim()
      } else {
        body.pierAppName  = form.pierAppName.trim()
        body.pierApiToken = form.pierApiToken
      }

      if (form.plexxerMode === 'existing') {
        body.plexxerAppId    = form.plexxerAppId.trim()
        body.plexxerApiToken = form.plexxerApiToken
      }
      // 'auto' and 'none' both leave plexxerAppId/Token undefined.
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
