import type { ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'

const labels: Record<string, string> = {
  Active: 'Etkin', PendingEnrollment: 'Kayıt bekliyor', Disabled: 'Devre dışı',
  Revoked: 'İptal edildi', Online: 'Çevrimiçi', Offline: 'Çevrimdışı',
  Unknown: 'Bilinmiyor', VersionMismatch: 'Protokol sürümü uyumsuz',
  online: 'Çevrimiçi', offline: 'Çevrimdışı', unknown: 'Bilinmiyor',
}

export function AgentState({ value }: { value: string }) {
  const state = value.toLowerCase()
  const tone = state === 'online' || state === 'active' ? 'success'
    : state === 'revoked' || state === 'versionmismatch' ? 'error'
      : state === 'disabled' ? 'warning' : 'neutral'
  return <Badge tone={tone} dot>{labels[value] ?? value}</Badge>
}

export function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return <div className="flex flex-wrap justify-between gap-2 border-b border-border py-2 last:border-0"><dt className="text-text-muted">{label}</dt><dd className="min-w-0 break-words text-text">{children ?? 'Henüz bildirilmedi'}</dd></div>
}
