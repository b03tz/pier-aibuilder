import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { vuetify } from './plugins/vuetify'
import { router } from './router'
import { useConfigStore } from './stores/config'

async function boot() {
  const app = createApp(App)
  const pinia = createPinia()
  app.use(pinia)

  // Load PUBLIC_* env BEFORE first render. The config store is needed by
  // routes that depend on runtime config (e.g. an external API base).
  await useConfigStore().load()

  app.use(vuetify)
  app.use(router)
  app.mount('#app')
}

boot().catch((e) => {
  // Fall back to a text-only error if boot fails — the Vue root never
  // mounted in this branch.
  const root = document.getElementById('app')
  if (root) root.innerHTML = `<pre style="font:14px/1.4 monospace;color:#ef6a6a;padding:24px;">AiBuilder failed to boot:\n\n${e?.message ?? e}</pre>`
})
