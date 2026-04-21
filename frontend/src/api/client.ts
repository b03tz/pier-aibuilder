// Tiny fetch wrapper. Paths are same-origin — they go through Vite's proxy
// in dev and hit the static-file-serving backend in prod. All requests send
// credentials so the auth cookie round-trips.

export interface ApiError extends Error {
  status: number
  body?: unknown
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const r = await fetch(path, {
    method,
    credentials: 'same-origin',
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
  createdAt: string
  updatedAt: string
}

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
