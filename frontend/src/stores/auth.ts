import { defineStore } from 'pinia'
import { api } from '../api/client'

interface Me { username: string }

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
    async login(username: string, password: string) {
      this.me = await api.post<Me>('/auth/login', { username, password })
    },
    async logout() {
      await api.post('/auth/logout')
      this.me = null
    },
  },
})
