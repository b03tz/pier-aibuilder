import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import vuetify from 'vite-plugin-vuetify'

export default defineConfig({
  plugins: [
    vue(),
    vuetify({ autoImport: true }),
  ],
  server: {
    port: 5173,
    // The backend owns /_pier/env.json, /auth/*, and /api/*. In dev the Vue
    // app runs on 5173 while the backend runs on 5218, so proxy through.
    proxy: {
      '/_pier':  { target: 'http://localhost:5218', changeOrigin: false },
      '/auth':   { target: 'http://localhost:5218', changeOrigin: false },
      '/api':    { target: 'http://localhost:5218', changeOrigin: false },
      '/_health':{ target: 'http://localhost:5218', changeOrigin: false },
    },
  },
})
