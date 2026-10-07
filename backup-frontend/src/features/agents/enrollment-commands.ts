export type AgentPlatform = 'Windows' | 'Linux'

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

export function enrollmentCommands(platform: AgentPlatform, server: string, token: string, name: string): string {
  const origin = httpsServerOrigin(server)
  if (!origin) return ''
  const quote = (value: string) => platform === 'Windows' ? `'${value.replaceAll("'", "''")}'` : `'${value.replaceAll("'", "'\\''")}'`
  const agentName = name.trim() ? ` --name ${quote(name.trim())}` : ''
  return platform === 'Windows'
    ? `.\\BackupPlatform.Agent.Host.exe enroll --server ${quote(origin)} --token ${quote(token)}${agentName} --non-interactive`
    : `./BackupPlatform.Agent.Host enroll --server ${quote(origin)} --token ${quote(token)}${agentName} --store-directory "$HOME/.local/share/backup-agent" --non-interactive\n./BackupPlatform.Agent.Host --store-directory "$HOME/.local/share/backup-agent"`
}
