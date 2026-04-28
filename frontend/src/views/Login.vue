<template>
  <v-container class="login-container" fluid>
    <v-card max-width="420" class="pa-6 mx-auto">
      <div class="d-flex align-center mb-4" style="gap: 14px;">
        <img src="/aibuilder-logo.svg" alt="AiBuilder" class="login-mark" />
        <div>
          <h1 class="text-h5" style="line-height: 1.1;">AiBuilder</h1>
          <p class="text-caption text-medium-emphasis ma-0">{{ config.env.PUBLIC_API_BASE || 'local dev' }}</p>
        </div>
      </div>
      <h2 class="text-subtitle-1 mb-4">Sign in</h2>

      <v-form v-if="!pendingId" @submit.prevent="onSubmitPassword">
        <v-text-field v-model="username" label="Username" autocomplete="username" class="mb-3" />
        <v-text-field v-model="password" label="Password" type="password" autocomplete="current-password" class="mb-4" />
        <v-alert v-if="error" type="error" density="compact" class="mb-3">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" block :loading="busy">Sign in</v-btn>
      </v-form>

      <v-form v-else @submit.prevent="onSubmitTotp">
        <p class="text-body-2 mb-3">
          Enter the 6-digit code from your authenticator app.
        </p>
        <v-text-field
          v-model="code"
          label="Authentication code"
          inputmode="numeric"
          autocomplete="one-time-code"
          maxlength="6"
          autofocus
          class="mb-4"
        />
        <v-alert v-if="error" type="error" density="compact" class="mb-3">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" block :loading="busy" :disabled="code.length !== 6">Verify</v-btn>
        <v-btn variant="text" size="small" block class="mt-2" @click="cancelTotp">Cancel and start over</v-btn>
      </v-form>
    </v-card>
  </v-container>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { useConfigStore } from '../stores/config'

const auth = useAuthStore()
const config = useConfigStore()
const router = useRouter()
const route = useRoute()

const username = ref('')
const password = ref('')
const code = ref('')
const pendingId = ref<string | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)

async function onSubmitPassword() {
  busy.value = true
  error.value = null
  try {
    const next = await auth.loginPassword(username.value, password.value)
    if (next) {
      pendingId.value = next
    } else {
      router.replace((route.query.next as string) || '/')
    }
  } catch (e: any) {
    if (e?.status === 429) error.value = 'Too many attempts. Try again in a few minutes.'
    else if (e?.status === 401) error.value = 'Invalid username or password'
    else error.value = e?.message ?? 'Login failed'
  } finally {
    busy.value = false
  }
}

async function onSubmitTotp() {
  if (!pendingId.value) return
  busy.value = true
  error.value = null
  try {
    await auth.loginTotp(pendingId.value, code.value)
    router.replace((route.query.next as string) || '/')
  } catch (e: any) {
    if (e?.status === 429) error.value = 'Too many attempts. Try again in a few minutes.'
    else if (e?.status === 401) error.value = 'Invalid or expired code'
    else error.value = e?.message ?? 'Verification failed'
    code.value = ''
  } finally {
    busy.value = false
  }
}

function cancelTotp() {
  pendingId.value = null
  code.value = ''
  password.value = ''
  error.value = null
}
</script>

<style scoped>
.login-container { min-height: 100vh; display: flex; align-items: center; }
.login-mark { width: 56px; height: 56px; display: block; }
</style>
