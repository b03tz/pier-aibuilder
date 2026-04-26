import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'

const routes = [
  { path: '/login',     name: 'login',    component: () => import('../views/Login.vue'), meta: { public: true } },
  { path: '/',          name: 'projects', component: () => import('../views/Projects.vue') },
  { path: '/projects/new', name: 'new-project', component: () => import('../views/NewProject.vue') },
  { path: '/projects/:id', name: 'project', component: () => import('../views/ProjectDetail.vue'), props: true },
  { path: '/settings',     name: 'settings', component: () => import('../views/Settings.vue') },
  { path: '/:path(.*)*', redirect: '/' },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

// Gate — anything not `meta.public` requires an auth cookie. We let the
// store cache the identity so we don't re-hit /auth/me on every navigation.
router.beforeEach(async (to) => {
  const auth = useAuthStore()
  if (!auth.checked) await auth.refresh()
  if (to.meta.public) return true
  if (!auth.signedIn) return { name: 'login', query: { next: to.fullPath } }
  return true
})
