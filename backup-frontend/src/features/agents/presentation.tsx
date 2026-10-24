import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'

export function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  useLanguage()
  return <div className="flex flex-wrap justify-between gap-2 border-b border-border py-2 last:border-0"><dt className="text-text-muted">{tx(label)}</dt><dd className="min-w-0 break-words text-text">{children ?? tx('Henüz bildirilmedi')}</dd></div>
}
