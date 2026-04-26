<template>
  <v-container>
    <h1 class="text-h5 mb-4">Settings</h1>

    <v-card class="pa-5 mb-4" max-width="720">
      <div class="d-flex align-center mb-3">
        <v-icon color="primary" class="mr-2">mdi-cloud-cog-outline</v-icon>
        <h3 class="text-h6 flex-grow-1">Pier integration</h3>
        <v-chip
          v-if="status?.configured"
          size="small"
          color="success"
          variant="tonal"
          prepend-icon="mdi-check-circle-outline"
        >
          Auto-deploy enabled
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

      <div v-if="status === null" class="text-medium-emphasis text-body-2">Loading…</div>

      <template v-else-if="status.configured">
        <div class="text-body-2">
          AiBuilder will automatically create a new Pier app when you tick
          <em>Create project on Pier</em> in the New Project form.
        </div>

        <v-divider class="my-3" />

        <div class="kv-row">
          <span class="kv-label">Admin token</span>
          <span class="kv-value"><code>padm_…{{ status.tokenLastFour ?? '????' }}</code></span>
        </div>
        <div class="kv-row">
          <span class="kv-label">Pier base</span>
          <span class="kv-value"><code>{{ status.base }}</code></span>
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
  </v-container>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, type PierAdminStatusDto } from '../api/client'

const status = ref<PierAdminStatusDto | null>(null)

onMounted(async () => {
  try {
    status.value = await api.get<PierAdminStatusDto>('/api/_pier-admin/status')
  } catch {
    status.value = { configured: false, base: '', tokenLastFour: null }
  }
})
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
