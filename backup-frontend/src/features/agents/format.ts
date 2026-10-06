/** Known identifiers reported by the agent; anything else is shown by its raw identifier so nothing is hidden. */
export const capabilityLabels: Record<string, string> = {
  'mssql.backup.full': 'MSSQL Full Backup',
  'mssql.backup.checksum': 'CHECKSUM',
  'mssql.backup.compression': 'Compression',
  'mssql.backup.copy-only': 'COPY_ONLY',
  'mssql.restore.verify-only': 'VERIFYONLY',
  'mssql.backup.differential': 'Differential backup',
  'mssql.backup.log': 'Log backup',
}

export const capabilitySupportLabels: Record<string, string> = {
  Supported: 'Destekleniyor',
  Unsupported: 'Desteklenmiyor',
  Unknown: 'Bilinmiyor',
}

export function agentDate(value: string | null | undefined) {
  return value ? new Date(value).toLocaleString('tr-TR') : 'Henüz bildirilmedi'
}
