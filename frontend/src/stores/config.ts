import { defineStore } from 'pinia'

// AiBuilder (and every generated app) must expose /_pier/env.json which
// carries the PUBLIC_* keys set on Pier. The frontend fetches it BEFORE
// first render and stashes the values here. Never hardcode URLs, model
// names, or feature flags in frontend source.
export type PublicEnv = Record<string, string>

export const useConfigStore = defineStore('config', {
  state: () => ({
    env: {} as PublicEnv,
    loaded: false,
  }),
  getters: {
    apiBase: (s) => s.env.PUBLIC_API_BASE ?? '',
  },
  actions: {
    async load() {
      if (this.loaded) return
      const r = await fetch('/_pier/env.json', { credentials: 'same-origin' })
      if (!r.ok) throw new Error(`/_pier/env.json → ${r.status}`)
      this.env = await r.json()
      this.loaded = true
    },
  },
})
