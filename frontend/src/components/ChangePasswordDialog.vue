<template>
  <v-dialog :model-value="modelValue" @update:model-value="emit('update:modelValue', $event)" max-width="440">
    <v-card class="pa-5">
      <h3 class="text-h6 mb-3">Change password</h3>
      <v-form @submit.prevent="onSubmit">
        <v-text-field
          v-model="oldPassword"
          label="Current password"
          type="password"
          autocomplete="current-password"
          class="mb-3"
        />
        <v-text-field
          v-model="newPassword"
          label="New password"
          type="password"
          autocomplete="new-password"
          hint="At least 8 characters"
          class="mb-3"
        />
        <v-text-field
          v-model="confirmPassword"
          label="Confirm new password"
          type="password"
          autocomplete="new-password"
          class="mb-3"
        />
        <v-alert v-if="error" type="error" density="compact" class="mb-3">{{ error }}</v-alert>
        <v-alert v-if="success" type="success" density="compact" class="mb-3">
          Password updated. You've been signed out — log in again with the new password.
        </v-alert>
        <div class="d-flex">
          <v-spacer />
          <v-btn variant="text" @click="onClose">Close</v-btn>
          <v-btn type="submit" color="primary" :loading="busy" :disabled="success">Change</v-btn>
        </div>
      </v-form>
    </v-card>
  </v-dialog>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../api/client'
import { useAuthStore } from '../stores/auth'

const props = defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{ (e: 'update:modelValue', v: boolean): void }>()

const auth = useAuthStore()
const router = useRouter()
const oldPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const success = ref(false)

// Reset the form whenever the dialog opens.
watch(() => props.modelValue, (open) => {
  if (open) {
    oldPassword.value = ''
    newPassword.value = ''
    confirmPassword.value = ''
    error.value = null
    success.value = false
  }
})

async function onSubmit() {
  error.value = null
  if (newPassword.value.length < 8) {
    error.value = 'New password must be at least 8 characters.'
    return
  }
  if (newPassword.value !== confirmPassword.value) {
    error.value = 'Passwords do not match.'
    return
  }
  if (oldPassword.value === newPassword.value) {
    error.value = 'New password must differ from the current one.'
    return
  }
  busy.value = true
  try {
    await api.post('/auth/change-password', {
      oldPassword: oldPassword.value,
      newPassword: newPassword.value,
    })
    // Backend signed us out so subsequent requests would 401. Reflect that
    // locally and punt to the login screen after a brief confirm-banner flash.
    success.value = true
    auth.me = null
    setTimeout(() => {
      emit('update:modelValue', false)
      router.push({ name: 'login' })
    }, 1200)
  } catch (e: any) {
    if (e?.status === 401) error.value = 'Current password is incorrect.'
    else error.value = e?.body?.error ?? e?.message ?? 'Password change failed.'
  } finally {
    busy.value = false
  }
}

function onClose() {
  emit('update:modelValue', false)
}
</script>
