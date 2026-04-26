// Mirror of Code/backend/AiBuilder.Api/Projects/Provisioning/PierAppSlug.cs.
//
// Used purely for live UI preview of "what slug will my project create
// on Pier?" while the user types — the backend re-derives canonically
// on submit, so even if these diverge by mistake the actual created
// slug always reflects backend logic. Keeping this in sync is a
// nice-to-have for accurate previews; it isn't a security boundary.

const PIER_REGEX = /^[a-z][a-z0-9-]{1,30}$/
const MAX = 31
const MIN = 2

export function derivePierAppSlug(projectName: string): string {
  let slug = normalize(projectName)
  if (slug.length === 0 || !/^[a-z]/.test(slug)) slug = 'app-' + slug
  if (slug.length > MAX) slug = slug.slice(0, MAX)
  slug = slug.replace(/-+$/, '')
  if (slug.length < MIN) slug = 'app'
  return slug
}

export function isValidPierAppSlug(slug: string): boolean {
  return PIER_REGEX.test(slug)
}

function normalize(raw: string): string {
  if (!raw) return ''
  const lower = raw.trim().toLowerCase()
  let out = ''
  let lastDash = false
  for (const ch of lower) {
    let c: string
    if (/[a-z0-9]/.test(ch))                    c = ch
    else if (ch === ' ' || ch === '_' ||
             ch === '.' || ch === '/' ||
             ch === '\\' || ch === '-')         c = '-'
    else                                        continue
    if (c === '-') {
      if (lastDash || out.length === 0) continue
      out += '-'
      lastDash = true
    } else {
      out += c
      lastDash = false
    }
  }
  return out.replace(/-+$/, '')
}
