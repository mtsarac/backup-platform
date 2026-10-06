export const capabilitySupportLabels: Record<string, string> = {
  Supported: 'Destekleniyor',
  Unsupported: 'Desteklenmiyor',
  Unknown: 'Bilinmiyor',
}

export function agentDate(value: string | null | undefined) {
  return value ? new Date(value).toLocaleString('tr-TR') : 'Henüz bildirilmedi'
}
