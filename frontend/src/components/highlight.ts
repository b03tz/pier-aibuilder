// Lazy Shiki wrapper. The highlighter is built on first call and reused.
// Language grammars and themes are loaded on demand via Shiki's dynamic
// imports — the initial JS payload stays small and only the languages we
// actually view get pulled in.
import type { Highlighter, BundledLanguage } from 'shiki'

let highlighterPromise: Promise<Highlighter> | null = null
const loadedLangs = new Set<BundledLanguage | 'text'>(['text'])

// Maps a filename to a Shiki BundledLanguage. Returns 'text' for binaries
// or unknown extensions — viewer will still show the content as plain text.
export function languageForFile(name: string): BundledLanguage | 'text' {
  const lower = name.toLowerCase()
  if (lower === 'dockerfile') return 'dockerfile'
  if (lower === '.gitignore' || lower === '.gitattributes' || lower === '.env' || lower.startsWith('.env.')) return 'bash'
  const dot = lower.lastIndexOf('.')
  const ext = dot >= 0 ? lower.slice(dot + 1) : ''
  switch (ext) {
    case 'cs':      return 'csharp'
    case 'csproj':
    case 'slnx':
    case 'sln':
    case 'xml':
    case 'axml':
    case 'config':  return 'xml'
    case 'vue':     return 'vue'
    case 'ts':
    case 'tsx':     return 'typescript'
    case 'js':
    case 'jsx':
    case 'mjs':
    case 'cjs':     return 'javascript'
    case 'json':
    case 'jsonc':   return 'json'
    case 'yml':
    case 'yaml':    return 'yaml'
    case 'md':
    case 'markdown': return 'markdown'
    case 'html':
    case 'htm':     return 'html'
    case 'css':     return 'css'
    case 'scss':    return 'scss'
    case 'sass':    return 'sass'
    case 'less':    return 'less'
    case 'sh':
    case 'bash':
    case 'zsh':     return 'bash'
    case 'py':      return 'python'
    case 'go':      return 'go'
    case 'rs':      return 'rust'
    case 'sql':     return 'sql'
    case 'toml':    return 'toml'
    case 'ini':     return 'ini'
    default:        return 'text'
  }
}

async function ensureHighlighter(): Promise<Highlighter> {
  if (!highlighterPromise) {
    const { createHighlighter } = await import('shiki')
    highlighterPromise = createHighlighter({
      themes: ['github-dark-dimmed'],
      langs: [],
    })
  }
  return highlighterPromise
}

export async function highlight(code: string, lang: BundledLanguage | 'text'): Promise<string> {
  const hl = await ensureHighlighter()
  if (lang !== 'text' && !loadedLangs.has(lang)) {
    try {
      await hl.loadLanguage(lang)
      loadedLangs.add(lang)
    } catch {
      // Grammar load failed — degrade to plain text rather than blowing up.
      lang = 'text'
    }
  }
  return hl.codeToHtml(code, { lang, theme: 'github-dark-dimmed' })
}
