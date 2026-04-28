<template>
  <v-container>
    <h1 class="text-h5 mb-4">Settings</h1>

    <v-card class="pa-5 mb-4" max-width="720">
      <div class="d-flex align-center mb-3">
        <v-icon color="primary" class="mr-2">mdi-shield-key-outline</v-icon>
        <h3 class="text-h6 flex-grow-1">Account security</h3>
        <v-chip
          v-if="auth.me?.totpEnabled"
          size="small"
          color="success"
          variant="tonal"
          prepend-icon="mdi-check-circle-outline"
        >
          2FA enabled
        </v-chip>
        <v-chip
          v-else
          size="small"
          color="warning"
          variant="tonal"
          prepend-icon="mdi-alert-circle-outline"
        >
          2FA off
        </v-chip>
      </div>

      <template v-if="auth.me?.totpEnabled">
        <div class="text-body-2">
          Sign-in requires a code from your authenticator app on top of your password.
        </div>
        <v-divider class="my-3" />
        <v-btn color="error" variant="outlined" @click="openDisable">Disable 2FA</v-btn>
      </template>

      <template v-else>
        <div v-if="!enrolPending">
          <div class="text-body-2 mb-3">
            Add a second factor to your sign-in. You will scan a QR code with an
            authenticator app (Google Authenticator, 1Password, Authy, Bitwarden,
            Aegis), then enter a 6-digit code to confirm.
          </div>
          <v-btn color="primary" :loading="enrolBusy" @click="startEnrol">Enable 2FA</v-btn>
        </div>

        <div v-else>
          <div class="text-body-2 mb-3">
            Scan this QR with your authenticator app, then enter the 6-digit code it shows.
          </div>
          <div class="d-flex align-start mb-4" style="gap: 16px;">
            <img :src="enrolPending.qrPngDataUri" alt="TOTP QR" style="background:#fff; padding:8px; border-radius:6px; width:200px; height:200px;" />
            <div class="flex-grow-1">
              <div class="kv-label">Manual code</div>
              <code class="d-block mt-1" style="word-break: break-all; font-size: 12px;">{{ enrolPending.secretBase32 }}</code>
              <v-btn size="x-small" variant="text" class="mt-1" prepend-icon="mdi-content-copy" @click="copy(enrolPending.secretBase32)">Copy</v-btn>
            </div>
          </div>

          <v-form @submit.prevent="confirmEnrol">
            <v-text-field
              v-model="enrolCode"
              label="6-digit code"
              inputmode="numeric"
              maxlength="6"
              autocomplete="one-time-code"
              autofocus
              class="mb-2"
            />
            <v-alert v-if="enrolError" type="error" density="compact" class="mb-3">{{ enrolError }}</v-alert>
            <div class="d-flex" style="gap: 8px;">
              <v-btn type="submit" color="primary" :loading="enrolBusy" :disabled="enrolCode.length !== 6">Confirm</v-btn>
              <v-btn variant="text" @click="cancelEnrol">Cancel</v-btn>
            </div>
          </v-form>
        </div>
      </template>
    </v-card>

    <v-dialog v-model="disableOpen" max-width="440" persistent>
      <v-card class="pa-5">
        <h3 class="text-h6 mb-3">Disable 2FA</h3>
        <p class="text-body-2 mb-3">
          Re-enter your current password to turn off two-factor sign-in.
        </p>
        <v-form @submit.prevent="confirmDisable">
          <v-text-field
            v-model="disablePassword"
            label="Current password"
            type="password"
            autocomplete="current-password"
            autofocus
            class="mb-2"
          />
          <v-alert v-if="disableError" type="error" density="compact" class="mb-3">{{ disableError }}</v-alert>
          <div class="d-flex justify-end" style="gap: 8px;">
            <v-btn variant="text" @click="closeDisable">Cancel</v-btn>
            <v-btn type="submit" color="error" :loading="disableBusy" :disabled="!disablePassword">Disable</v-btn>
          </div>
        </v-form>
      </v-card>
    </v-dialog>

    <v-card class="pa-5 mb-4" max-width="720">
      <div class="d-flex align-center mb-3">
        <v-icon color="primary" class="mr-2">mdi-cloud-cog-outline</v-icon>
        <h3 class="text-h6 flex-grow-1">Pier integration</h3>
        <v-chip
          v-if="pierStatus?.configured"
          size="small"
          color="success"
          variant="tonal"
          prepend-icon="mdi-check-circle-outline"
        >
          Auto-create enabled
        </v-chip>
        <v-chip
          v-else
          size="small"
          color="warning"
          variant="tonal"
          prepend-icon="mdi-alert-circle-outline"
        >
          Not configured
        </v-chip>
      </div>

      <div v-if="pierStatus === null" class="text-medium-emphasis text-body-2">Loading…</div>

      <template v-else-if="pierStatus.configured">
        <div class="text-body-2">
          AiBuilder will automatically create a new Pier app when you pick
          <em>Create new</em> under <em>Pier hosting</em> in the New Project form.
        </div>

        <v-divider class="my-3" />

        <div class="kv-row">
          <span class="kv-label">Admin token</span>
          <span class="kv-value"><code>padm_…{{ pierStatus.tokenLastFour ?? '????' }}</code></span>
        </div>
        <div class="kv-row">
          <span class="kv-label">Pier base</span>
          <span class="kv-value"><code>{{ pierStatus.base }}</code></span>
        </div>
      </template>

      <template v-else>
        <v-alert type="info" variant="tonal" density="compact" class="mb-3">
          Set <code>PIER_ADMIN_TOKEN</code> (and, if you need to override the
          default <code>http://127.0.0.1:8080</code>, <code>PIER_ADMIN_BASE</code>)
          on the AiBuilder Pier app's env vars and restart it. Until then,
          new projects must be bound to a manually-provisioned Pier app.
        </v-alert>
        <ol class="text-body-2 ml-4">
          <li>In Pier's admin UI, open <strong>Settings</strong> and mint a fresh <code>padm_…</code> token.</li>
          <li>Open the AiBuilder app's env vars panel; add <code>PIER_ADMIN_TOKEN</code> (secret).</li>
          <li>Restart AiBuilder.</li>
          <li>Refresh this page — the indicator should flip to green.</li>
        </ol>
      </template>
    </v-card>

    <v-card class="pa-5 mb-4" max-width="720">
      <div class="d-flex align-center mb-3">
        <v-icon color="primary" class="mr-2">mdi-database-cog-outline</v-icon>
        <h3 class="text-h6 flex-grow-1">Plexxer integration</h3>
        <v-chip
          v-if="plexxerStatus?.configured"
          size="small"
          color="success"
          variant="tonal"
          prepend-icon="mdi-check-circle-outline"
        >
          Auto-create enabled
        </v-chip>
        <v-chip
          v-else
          size="small"
          color="warning"
          variant="tonal"
          prepend-icon="mdi-alert-circle-outline"
        >
          Not configured
        </v-chip>
      </div>

      <div v-if="plexxerStatus === null" class="text-medium-emphasis text-body-2">Loading…</div>

      <template v-else-if="plexxerStatus.configured">
        <div class="text-body-2">
          AiBuilder will automatically create a fresh Plexxer app and mint a
          per-app token whenever you pick <em>Create new</em> under
          <em>Plexxer database</em> in the New Project form.
        </div>

        <v-divider class="my-3" />

        <div class="kv-row">
          <span class="kv-label">Account token</span>
          <span class="kv-value"><code>plx_…{{ plexxerStatus.tokenLastFour ?? '????' }}</code></span>
        </div>
        <div class="kv-row">
          <span class="kv-label">Plexxer base</span>
          <span class="kv-value"><code>{{ plexxerStatus.base }}</code></span>
        </div>
      </template>

      <template v-else>
        <v-alert type="info" variant="tonal" density="compact" class="mb-3">
          Set <code>PLEXXER_ACCOUNT_TOKEN</code> on the AiBuilder Pier app's
          env vars and restart it. Until then, new projects either use an
          existing Plexxer app (paste app id + token) or no Plexxer DB at all.
        </v-alert>
        <ol class="text-body-2 ml-4">
          <li>In Plexxer's dashboard, mint an <strong>account-scoped</strong> token (avatar menu → Account tokens → Mint).</li>
          <li>Required grants: <code>account:apps:w</code> + <code>account:tokens:w</code>.</li>
          <li>Open the AiBuilder app's env vars panel in Pier; add <code>PLEXXER_ACCOUNT_TOKEN</code> (secret).</li>
          <li>Restart AiBuilder. Refresh this page — the indicator should flip to green.</li>
        </ol>
      </template>
    </v-card>
  </v-container>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, type PierAdminStatusDto, type PlexxerAdminStatusDto } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()

const pierStatus    = ref<PierAdminStatusDto    | null>(null)
const plexxerStatus = ref<PlexxerAdminStatusDto | null>(null)

interface TotpSetupDto { secretBase32: string; otpAuthUri: string; qrPngDataUri: string }
const enrolPending = ref<TotpSetupDto | null>(null)
const enrolCode    = ref('')
const enrolBusy    = ref(false)
const enrolError   = ref<string | null>(null)

const disableOpen     = ref(false)
const disablePassword = ref('')
const disableBusy     = ref(false)
const disableError    = ref<string | null>(null)

onMounted(async () => {
  const [p, x] = await Promise.allSettled([
    api.get<PierAdminStatusDto>('/api/_pier-admin/status'),
    api.get<PlexxerAdminStatusDto>('/api/_plexxer-admin/status'),
  ])
  pierStatus.value    = p.status === 'fulfilled' ? p.value : { configured: false, base: '', tokenLastFour: null }
  plexxerStatus.value = x.status === 'fulfilled' ? x.value : { configured: false, base: '', tokenLastFour: null }
  // Refresh /auth/me so the chip reflects current totpEnabled even if the
  // user toggled it from another tab.
  await auth.refresh()
})

async function startEnrol() {
  enrolBusy.value = true
  enrolError.value = null
  try {
    enrolPending.value = await api.post<TotpSetupDto>('/auth/totp/setup')
    enrolCode.value = ''
  } catch (e: any) {
    enrolError.value = e?.message ?? 'Could not start enrolment'
  } finally {
    enrolBusy.value = false
  }
}

async function confirmEnrol() {
  enrolBusy.value = true
  enrolError.value = null
  try {
    await api.post('/auth/totp/confirm', { code: enrolCode.value })
    enrolPending.value = null
    enrolCode.value = ''
    await auth.refresh()
  } catch (e: any) {
    const code = e?.body?.error
    if (code === 'invalid-code') enrolError.value = 'Invalid code. Try again — the QR is still valid.'
    else if (code === 'no-pending-enrolment') enrolError.value = 'Enrolment expired. Click Enable 2FA again.'
    else enrolError.value = e?.message ?? 'Confirmation failed'
    enrolCode.value = ''
  } finally {
    enrolBusy.value = false
  }
}

function cancelEnrol() {
  enrolPending.value = null
  enrolCode.value = ''
  enrolError.value = null
}

function openDisable() {
  disableOpen.value = true
  disablePassword.value = ''
  disableError.value = null
}

function closeDisable() {
  disableOpen.value = false
  disablePassword.value = ''
  disableError.value = null
}

async function confirmDisable() {
  disableBusy.value = true
  disableError.value = null
  try {
    await api.post('/auth/totp/disable', { password: disablePassword.value })
    disableOpen.value = false
    disablePassword.value = ''
    await auth.refresh()
  } catch (e: any) {
    if (e?.status === 401) disableError.value = 'Incorrect password'
    else disableError.value = e?.message ?? 'Disable failed'
  } finally {
    disableBusy.value = false
  }
}

async function copy(v: string) {
  try { await navigator.clipboard.writeText(v) } catch { /* ignore */ }
}
</script>

<style scoped>
.kv-row {
  display: flex;
  padding: 4px 0;
  font-size: 13px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
}
.kv-row:last-child { border-bottom: 0; }
.kv-label { width: 140px; opacity: 0.55; text-transform: uppercase; letter-spacing: 0.5px; font-size: 11px; padding-top: 2px; }
.kv-value { flex: 1; }
.kv-value code { font-family: ui-monospace, Menlo, monospace; background: rgba(106,168,255,0.10); color: #9ecbff; padding: 1px 6px; border-radius: 4px; font-size: 12px; }
</style>
