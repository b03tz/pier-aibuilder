<template>
  <v-container>
    <h1 class="text-h5 mb-4">{{ form.isImport ? 'Import existing project' : 'New project' }}</h1>
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
        <v-text-field
          v-model="form.pierAppName"
          label="Pier app name (subdomain) *"
          :hint="form.isImport
            ? 'The existing Pier subdomain — must match ^[a-z][a-z0-9-]{0,39}$'
            : 'Must match ^[a-z][a-z0-9-]{0,39}$'"
          class="mb-3"
        />
        <v-text-field
          v-model="form.pierApiToken"
          :label="form.isImport ? 'Existing Pier API token *' : 'Pier API token *'"
          type="password"
          class="mb-3"
        />

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
          <v-btn color="primary" type="submit" :loading="busy">
            {{ form.isImport ? 'Import' : 'Create' }}
          </v-btn>
        </div>
      </v-form>
    </v-card>
  </v-container>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, type CreateProjectResponse } from '../api/client'

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
})
const busy = ref(false)
const error = ref<string | null>(null)
const router = useRouter()

async function onSubmit() {
  busy.value = true
  error.value = null
  try {
    // Normalise empty strings → undefined so the backend sees "not provided"
    // instead of "provided empty". Helps the both-or-neither check pass.
    const body = {
      name:            form.name,
      pierAppName:     form.pierAppName,
      pierApiToken:    form.pierApiToken,
      scopeBrief:      form.scopeBrief,
      isImport:        form.isImport,
      plexxerAppId:    form.plexxerAppId.trim()    || undefined,
      plexxerApiToken: form.plexxerApiToken.trim() || undefined,
      gitRemoteUrl:    form.gitRemoteUrl.trim()    || undefined,
      gitRemoteBranch: form.gitRemoteBranch.trim() || undefined,
    }
    const r = await api.post<CreateProjectResponse>('/api/projects', body)
    // If the import had a partial failure (e.g. clone rejected) we still
    // navigate — the project exists and the VCS tab can recover. The detail
    // page's tabs will surface what went wrong.
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
    if (body.message) return `${body.error ?? 'error'}: ${body.message}`
    if (body.reason)  return `${body.error ?? 'error'}: ${body.reason}`
    if (body.error)   return body.error
  }
  return e?.message ?? 'Create failed'
}
</script>
