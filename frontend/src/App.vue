<template>
  <v-app>
    <v-app-bar density="compact" flat border>
      <v-app-bar-title>
        <router-link to="/" class="logo">AiBuilder</router-link>
      </v-app-bar-title>
      <v-spacer />
      <template v-if="auth.signedIn">
        <v-menu>
          <template #activator="{ props: menuProps }">
            <v-btn v-bind="menuProps" variant="text" append-icon="mdi-chevron-down">
              {{ auth.me?.username }}
            </v-btn>
          </template>
          <v-list density="compact">
            <v-list-item prepend-icon="mdi-key-variant" title="Change password" @click="showChangePwd = true" />
            <v-list-item prepend-icon="mdi-logout" title="Log out" @click="onLogout" />
          </v-list>
        </v-menu>
      </template>
    </v-app-bar>
    <v-main>
      <router-view />
    </v-main>
    <ChangePasswordDialog v-model="showChangePwd" />
  </v-app>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from './stores/auth'
import ChangePasswordDialog from './components/ChangePasswordDialog.vue'

const auth = useAuthStore()
const router = useRouter()
const showChangePwd = ref(false)

async function onLogout() {
  await auth.logout()
  router.push({ name: 'login' })
}
</script>

<style scoped>
.logo { color: inherit; text-decoration: none; font-weight: 600; letter-spacing: 0.4px; }
</style>
