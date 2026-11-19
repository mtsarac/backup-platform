import type { SupportedLanguage } from '@/lib/i18n/types'

export type AgentPlatform = 'Windows' | 'Linux'

/** What a visitor most likely installs on: Linux only from a Linux desktop browser, otherwise Windows. */
export const likelyPlatform = (userAgent = navigator.userAgent): AgentPlatform => /linux|x11/i.test(userAgent) && !/android|cros/i.test(userAgent) ? 'Linux' : 'Windows'

/** Commands accept an origin, never credentials, paths, queries or an insecure URL. */
export function httpsServerOrigin(value: string): string {
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && !url.username && !url.password && url.pathname === '/' && !url.search && !url.hash ? url.origin : ''
  } catch { return '' }
}

export function enrollmentCommands(platform: AgentPlatform, server: string, token: string, name: string, language: SupportedLanguage = 'tr'): string {
  const origin = httpsServerOrigin(server)
  if (!origin || !token || !name.trim()) return ''
  const quote = (value: string) => platform === 'Windows' ? `'${value.replaceAll("'", "''")}'` : `'${value.replaceAll("'", "'\\''")}'`
  if (platform === 'Windows') {
    const parameters = `-Token ${quote(token)} -AgentName ${quote(name.trim())} -NonInteractive`
    return `Invoke-RestMethod -Uri ${quote(`${origin}/api/agent-dist/install.ps1`)} -OutFile "$env:TEMP\\install.ps1"; powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\\install.ps1" -Server ${quote(origin)} ${parameters} -Language ${language}`
  }
  const parameters = `--token ${quote(token)} --name ${quote(name.trim())} --non-interactive`
  return `(set -e; script=$(mktemp); trap 'rm -f "$script"' EXIT; curl --proto '=https' --tlsv1.2 -fsSL ${quote(`${origin}/api/agent-dist/install.sh`)} -o "$script"; sudo bash "$script" --server ${quote(origin)} ${parameters} --language ${language})`
}
