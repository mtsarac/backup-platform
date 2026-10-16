import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'

const labels: Record<string, string> = {
  get Active() { return tx("Etkin") }, get PendingEnrollment() { return tx("Kayıt bekliyor") }, get Disabled() { return tx("Devre dışı") },
  get Revoked() { return tx("İptal edildi") }, get Online() { return tx("Çevrimiçi") }, get Offline() { return tx("Çevrimdışı") },
  get Unknown() { return tx("Bilinmiyor") }, get VersionMismatch() { return tx("Protokol sürümü uyumsuz") },
  get online() { return tx("Çevrimiçi") }, get offline() { return tx("Çevrimdışı") }, get unknown() { return tx("Bilinmiyor") },
}

export function AgentState({ value }: { value: string }) {
  useLanguage()
  const state = value.toLowerCase()
  const tone = state === 'online' || state === 'active' ? 'success'
    : state === 'revoked' || state === 'versionmismatch' ? 'error'
      : state === 'disabled' ? 'warning' : 'neutral'
  return <Badge tone={tone} dot>{labels[value] ?? value}</Badge>
}

export function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  useLanguage()
  return <div className="flex flex-wrap justify-between gap-2 border-b border-border py-2 last:border-0"><dt className="text-text-muted">{tx(label)}</dt><dd className="min-w-0 break-words text-text">{children ?? tx('Henüz bildirilmedi')}</dd></div>
}
