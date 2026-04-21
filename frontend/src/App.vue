<template>
  <v-app>
    <v-app-bar density="compact" flat border>
      <v-app-bar-title>
        <router-link to="/" class="logo">AiBuilder</router-link>
      </v-app-bar-title>
      <v-spacer />
      <template v-if="auth.signedIn">
        <span class="user-label">{{ auth.me?.username }}</span>
        <v-btn icon="mdi-logout" size="small" @click="onLogout" />
      </template>
    </v-app-bar>
    <v-main>
      <router-view />
    </v-main>
  </v-app>
</template>

<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useAuthStore } from './stores/auth'

const auth = useAuthStore()
const router = useRouter()

async function onLogout() {
  await auth.logout()
  router.push({ name: 'login' })
}
</script>

<style scoped>
.logo { color: inherit; text-decoration: none; font-weight: 600; letter-spacing: 0.4px; }
.user-label { opacity: 0.75; margin-right: 12px; font-size: 13px; }
</style>
