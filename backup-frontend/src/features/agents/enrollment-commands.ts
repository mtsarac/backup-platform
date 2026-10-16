import type { SupportedLanguage } from '@/lib/i18n/types'

export type AgentPlatform = 'Windows' | 'Linux'
export type InstallationMode = 'interactive' | 'automatic'

/** Commands accept an origin, never credentials, paths, queries or an insecure URL. */
export function httpsServerOrigin(value: string): string {
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && !url.username && !url.password && url.pathname === '/' && !url.search && !url.hash ? url.origin : ''
  } catch { return '' }
}

export function suggestedServerOrigin(apiBase: string, currentOrigin: string): string {
  try {
    const url = new URL(apiBase, currentOrigin)
    return url.username || url.password ? '' : httpsServerOrigin(url.origin)
  } catch { return '' }
}

export function enrollmentCommands(platform: AgentPlatform, server: string, token: string, name: string, mode: InstallationMode = 'automatic', language: SupportedLanguage = 'tr'): string {
  const origin = httpsServerOrigin(server)
  if (!origin || (mode === 'automatic' && (!token || !name.trim()))) return ''
  const quote = (value: string) => platform === 'Windows' ? `'${value.replaceAll("'", "''")}'` : `'${value.replaceAll("'", "'\\''")}'`
  if (platform === 'Windows') {
    const parameters = mode === 'interactive' ? '-Interactive' : `-Token ${quote(token)} -AgentName ${quote(name.trim())} -NonInteractive`
    return `& ([scriptblock]::Create((Invoke-RestMethod -Uri ${quote(`${origin}/api/agent-dist/install.ps1`)}))) -Server ${quote(origin)} ${parameters} -Language ${language}`
  }
  const parameters = mode === 'interactive' ? '--interactive' : `--token ${quote(token)} --name ${quote(name.trim())} --non-interactive`
  return `(set -e; script=$(mktemp); trap 'rm -f "$script"' EXIT; curl --proto '=https' --tlsv1.2 -fsSL ${quote(`${origin}/api/agent-dist/install.sh`)} -o "$script"; sudo bash "$script" --server ${quote(origin)} ${parameters} --language ${language})`
}
