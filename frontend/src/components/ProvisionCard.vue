<template>
  <v-card variant="outlined" class="pa-4 fill-height" :class="cardClass">
    <div class="d-flex align-center mb-3">
      <v-icon :icon="icon" class="mr-2" />
      <div class="text-subtitle-1 flex-grow-1">{{ title }}</div>
      <v-chip v-if="status?.configured" size="x-small" color="success" variant="tonal" prepend-icon="mdi-check">
        ready · …{{ status.tokenLastFour ?? '????' }}
      </v-chip>
      <v-chip v-else size="x-small" color="warning" variant="tonal" prepend-icon="mdi-alert">
        not configured
      </v-chip>
    </div>

    <v-btn-toggle
      :model-value="mode"
      @update:model-value="onModeChange"
      mandatory
      density="compact"
      color="primary"
      variant="outlined"
      class="mb-3"
      style="width: 100%"
    >
      <v-btn
        :value="'auto'"
        :disabled="!status?.configured"
        size="small"
        style="flex: 1"
      >
        Create new
      </v-btn>
      <v-btn :value="'existing'" size="small" style="flex: 1">
        Use existing
      </v-btn>
      <v-btn v-if="allowNone" :value="'none'" size="small" style="flex: 1">
        None
      </v-btn>
    </v-btn-toggle>

    <div class="card-body">
      <slot v-if="mode === 'auto'"     name="auto" />
      <slot v-else-if="mode === 'none'" name="empty" />
      <slot v-else                       name="existing" />
    </div>

    <div v-if="!status?.configured && mode === 'auto'" class="text-caption text-medium-emphasis mt-2">
      <v-icon icon="mdi-information-outline" size="x-small" class="mr-1" />
      Set <code>{{ providerEnvVar }}</code> in Pier env to enable.
      <router-link to="/settings">Open Settings →</router-link>
    </div>
  </v-card>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { PierAdminStatusDto } from '../api/client'

type ProvisionMode = 'auto' | 'existing' | 'none'

const props = defineProps<{
  title: string
  icon: string
  providerEnvVar: string
  status: PierAdminStatusDto | null
  mode: ProvisionMode
  slugPreview: string
  allowNone?: boolean
}>()

const emit = defineEmits<{
  (e: 'update:mode', m: ProvisionMode): void
}>()

function onModeChange(m: ProvisionMode | null) {
  // v-btn-toggle with `mandatory` shouldn't emit null, but Vuetify
  // sometimes does during transitions. Coerce so the parent always
  // sees a valid mode.
  if (m) emit('update:mode', m)
}

const cardClass = computed(() => props.mode === 'auto' ? 'card-auto' : 'card-existing')
</script>

<style scoped>
.card-auto {
  border-color: rgba(106, 168, 255, 0.4);
}
.card-existing {
  border-color: rgba(255, 255, 255, 0.08);
}
.card-body {
  min-height: 80px;
}
</style>
