<template>
  <v-container>
    <h1 class="text-h5 mb-4">New project</h1>
    <v-card class="pa-6" max-width="720">
      <v-form @submit.prevent="onSubmit">
        <v-text-field v-model="form.name" label="Project name" class="mb-3" />
        <v-text-field
          v-model="form.pierAppName"
          label="Pier app name (subdomain)"
          hint="Must match ^[a-z][a-z0-9-]{0,39}$"
          class="mb-3"
        />
        <v-text-field v-model="form.pierApiToken" label="Pier API token" type="password" class="mb-3" />
        <v-text-field v-model="form.plexxerAppId" label="Plexxer app ID" class="mb-3" />
        <v-text-field v-model="form.plexxerApiToken" label="Plexxer API token" type="password" class="mb-3" />
        <v-textarea
          v-model="form.scopeBrief"
          label="Scope brief"
          hint="What should this app do? One-liner is fine — you'll refine in the scope conversation."
          rows="4"
          class="mb-4"
        />
        <v-alert v-if="error" type="error" class="mb-3">{{ error }}</v-alert>
        <div class="d-flex">
          <v-btn to="/" variant="text">Cancel</v-btn>
          <v-spacer />
          <v-btn color="primary" type="submit" :loading="busy">Create</v-btn>
        </div>
      </v-form>
    </v-card>
  </v-container>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, type ProjectDto } from '../api/client'

const form = reactive({
  name: '',
  pierAppName: '',
  pierApiToken: '',
  plexxerAppId: '',
  plexxerApiToken: '',
  scopeBrief: '',
})
const busy = ref(false)
const error = ref<string | null>(null)
const router = useRouter()

async function onSubmit() {
  busy.value = true
  error.value = null
  try {
    const p = await api.post<ProjectDto>('/api/projects', form)
    router.push({ name: 'project', params: { id: p.id } })
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
    if (body.error)   return body.error
  }
  return e?.message ?? 'Create failed'
}
</script>
