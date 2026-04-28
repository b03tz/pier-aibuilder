import { defineStore } from 'pinia'
import { api } from '../api/client'

interface Me { username: string; totpEnabled: boolean }

interface LoginPasswordResponse {
  requiresTotp: boolean
  pendingId?: string
  username?: string
}

export const useAuthStore = defineStore('auth', {
  state: () => ({
    me: null as Me | null,
    checked: false,
  }),
  getters: {
    signedIn: (s) => s.me !== null,
  },
  actions: {
    async refresh() {
      try {
        this.me = await api.get<Me>('/auth/me')
      } catch {
        this.me = null
      } finally {
        this.checked = true
      }
    },
    // Step 1. Returns null on a complete sign-in (legacy or non-2FA admin)
    // and a pendingId when the second factor is required. The cookie is NOT
    // set in the latter case.
    async loginPassword(username: string, password: string): Promise<string | null> {
      const r = await api.post<LoginPasswordResponse>('/auth/login', { username, password })
      if (r.requiresTotp && r.pendingId) return r.pendingId
      // Cookie was issued — fetch /auth/me so we have totpEnabled too.
      await this.refresh()
      return null
    },
    // Step 2. Completes sign-in via the pendingId from step 1.
    async loginTotp(pendingId: string, code: string) {
      await api.post('/auth/login/totp', { pendingId, code })
      await this.refresh()
    },
    async logout() {
      await api.post('/auth/logout')
      this.me = null
    },
  },
})
