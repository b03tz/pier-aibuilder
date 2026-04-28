// Tiny fetch wrapper. In dev: paths are same-origin and go through Vite's
// proxy to the backend. In prod on Pier: the frontend serves from
// <app>.onpier.tech and the backend from api-<app>.onpier.tech — so we
// prefix with `PUBLIC_API_BASE` when it's set to something other than this
// same origin. `credentials: 'include'` sends the cookie in both cases.
import { useConfigStore } from '../stores/config'

export interface ApiError extends Error {
  status: number
  body?: unknown
}

function resolveUrl(path: string): string {
  if (path.startsWith('http')) return path
  let base = ''
  try { base = useConfigStore().apiBase } catch { /* store not ready during boot */ }
  if (!base) return path
  if (typeof window !== 'undefined' && base === window.location.origin) return path
  return base.replace(/\/$/, '') + path
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const r = await fetch(resolveUrl(path), {
    method,
    credentials: 'include',
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  })
  const text = await r.text()
  const parsed = text ? safeJson(text) : null
  if (!r.ok) {
    const err = new Error(`${method} ${path} → ${r.status}`) as ApiError
    err.status = r.status
    err.body = parsed ?? text
    throw err
  }
  return (parsed ?? ({} as unknown)) as T
}

function safeJson(s: string): unknown {
  try { return JSON.parse(s) } catch { return s }
}

export const api = {
  get:    <T>(p: string)             => request<T>('GET',    p),
  post:   <T>(p: string, b?: unknown) => request<T>('POST',   p, b),
  put:    <T>(p: string, b?: unknown) => request<T>('PUT',    p, b),
  patch:  <T>(p: string, b?: unknown) => request<T>('PATCH',  p, b),
  delete: <T>(p: string)             => request<T>('DELETE', p),
}

// Shared shapes. Kept in one place so the views read consistently.
export interface ProjectDto {
  id: string
  name: string
  pierAppName: string
  plexxerAppId: string
  scopeBrief: string
  workspaceStatus: string
  isImported: boolean
  createdAt: string
  updatedAt: string
}

// Per-mode side-effect report from POST /api/projects. Only present when
// the request opted into Import mode; describes how clone, env-var mirror,
// and introspection went so the UI can warn on partial failure.
export interface ImportReportDto {
  mode: 'new' | 'import'
  cloneOk: boolean | null
  cloneError: string | null
  envMirroredCount: number | null
  envSkippedCount: number | null
  envMirrorError: string | null
  introspectionOk: boolean | null
  introspectionError: string | null
}

// Surfaced on the auto-create branch (Pier and/or Plexxer). Each pair of
// fields is null when the corresponding side wasn't auto-created (the
// user pasted existing creds for that side).
export interface ProvisioningReportDto {
  // Pier side
  pierAutoCreated: boolean
  pierAppName: string | null
  apiBaseUrl: string | null
  apiDomain: string | null
  frontendDomain: string | null
  envSeedOk: boolean | null
  envSeedError: string | null
  // Plexxer side
  plexxerAutoCreated: boolean
  plexxerAppKey: string | null
}

export interface CreateProjectResponse {
  project: ProjectDto
  import: ImportReportDto | null
  provisioning: ProvisioningReportDto | null
}

// GET /api/_pier-admin/status + /api/_plexxer-admin/status — used by the
// Settings page and NewProject to decide whether to enable the "Create
// new" toggles. Same shape on both sides; never includes the full token,
// only the last four chars for spot-check display.
export interface PierAdminStatusDto {
  configured: boolean
  base: string
  tokenLastFour: string | null
}

export type PlexxerAdminStatusDto = PierAdminStatusDto

export interface TurnDto {
  id: string
  role: string
  content: string
  turnIndex: number
  createdAt: string
}

export interface BuildRunDto {
  id: string
  kind: string
  status: string
  failureReason: string | null
  transcriptPath: string
  startedAt: string
  finishedAt: string | null
}

export interface WorkspaceNodeDto {
  path: string
  name: string
  isDir: boolean
  size: number | null
}

export interface EnvVarDto {
  key: string
  value: string | null
  isSecret: boolean
  exposeToFrontend: boolean
  updatedAt: string
}

export interface DeployRunDto {
  id: string
  status: string
  failureReason: string | null
  pierDeployVersion: number | null
  pierFrontendDeployVersion: number | null
  deployNotes: string | null
  startedAt: string
  finishedAt: string | null
}

export interface VcsStateDto {
  remoteUrl: string | null
  branch: string | null
  currentBranch: string | null
  headSha: string | null
  headShortSha: string | null
  headSubject: string | null
  headAt: string | null
  lastPushSha: string | null
  lastPushAt: string | null
  isImported: boolean
  workspaceHasGit: boolean
}

export interface CloneResponse {
  ok: boolean
  error: string | null
  envMirroredCount: number | null
  envMirrorError: string | null
  introspectionOk: boolean | null
  introspectionError: string | null
}

export interface PullResponse {
  ok: boolean
  errorCode: string | null
  errorMessage: string | null
  previousSha: string | null
  newSha: string | null
  filesChanged: number
  uncommittedFiles: string[]
  output: string
  state: VcsStateDto | null
}
