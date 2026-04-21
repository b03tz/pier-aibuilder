<template>
  <v-container class="login-container" fluid>
    <v-card max-width="420" class="pa-6 mx-auto">
      <h1 class="text-h5 mb-1">Sign in</h1>
      <p class="text-body-2 text-medium-emphasis mb-5">AiBuilder · {{ config.env.PUBLIC_API_BASE || 'local dev' }}</p>
      <v-form @submit.prevent="onSubmit">
        <v-text-field v-model="username" label="Username" autocomplete="username" class="mb-3" />
        <v-text-field v-model="password" label="Password" type="password" autocomplete="current-password" class="mb-4" />
        <v-alert v-if="error" type="error" density="compact" class="mb-3">{{ error }}</v-alert>
        <v-btn type="submit" color="primary" block :loading="busy">Sign in</v-btn>
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
const busy = ref(false)
const error = ref<string | null>(null)

async function onSubmit() {
  busy.value = true
  error.value = null
  try {
    await auth.login(username.value, password.value)
    const next = (route.query.next as string) || '/'
    router.replace(next)
  } catch (e: any) {
    error.value = e?.status === 401 ? 'Invalid username or password' : e?.message ?? 'Login failed'
  } finally {
    busy.value = false
  }
}
</script>

<style scoped>
.login-container { min-height: 100vh; display: flex; align-items: center; }
</style>
