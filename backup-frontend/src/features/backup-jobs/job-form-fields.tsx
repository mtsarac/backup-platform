import type { ReactNode } from 'react'

export function FormGrid({ children }: { children: ReactNode }) {
  return <div className="grid gap-4 md:grid-cols-2 [&>div]:min-w-0">{children}</div>
}

/** Collapsed-by-default technical settings; opens when one of its fields has an error. */
export function Advanced({ title, open, children }: { title: string; open?: boolean; children: ReactNode }) {
  return <details open={open || undefined} className="rounded-xl border border-border bg-surface shadow-card">
    <summary className="cursor-pointer px-5 py-4 font-semibold text-text-heading">{title} <span className="text-sm font-normal text-text-muted">(isteğe bağlı, teknik ekipler için)</span></summary>
    <div className="border-t border-border p-5"><FormGrid>{children}</FormGrid></div>
  </details>
}

