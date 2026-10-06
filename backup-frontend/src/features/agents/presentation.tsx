import type { ReactNode } from 'react'

const labels: Record<string, string> = {
  Active: 'Etkin', PendingEnrollment: 'Kayıt bekliyor', Disabled: 'Devre dışı',
  Revoked: 'İptal edildi', Online: 'Çevrimiçi', Offline: 'Çevrimdışı',
  Unknown: 'Bilinmiyor', VersionMismatch: 'Protokol sürümü uyumsuz',
  online: 'Çevrimiçi', offline: 'Çevrimdışı', unknown: 'Bilinmiyor',
}


export function AgentState({ value }: { value: string }) {
  const state = value.toLowerCase()
  const tone = state === 'online' || state === 'active'
    ? 'bg-success-subtle text-success'
    : state === 'revoked' || state === 'versionmismatch'
      ? 'bg-error-subtle text-error'
      : state === 'disabled' ? 'bg-warning-subtle text-warning' : 'bg-surface-2 text-text-muted'
  return <span className={`inline-flex rounded-full px-2 py-1 text-xs font-medium ${tone}`}>{labels[value] ?? value}</span>
}


export function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return <div className="flex flex-wrap justify-between gap-2 border-b border-border py-2 last:border-0"><dt className="text-text-muted">{label}</dt><dd className="min-w-0 break-words text-text">{children ?? 'Henüz bildirilmedi'}</dd></div>
}
