import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'

export function FormGrid({ children }: { children: ReactNode }) {
  useLanguage()
  return <div className="grid gap-4 md:grid-cols-2 [&>div]:min-w-0">{children}</div>
}

/** One wizard question with the short reason it is asked. */
export function Question({ title, why, children }: { title: string; why: string; children?: ReactNode }) {
  useLanguage()
  return <Section title={title} description={<><span className="font-medium text-text-heading">{tx("Neden soruluyor?")}</span>{' '}{why}</>}>{children}</Section>
}

/** Collapsed-by-default settings with safe defaults; opens when one of its fields has an error. */
export function Advanced({ open, children }: { open?: boolean; children: ReactNode }) {
  useLanguage()
  return <details open={open || undefined} className="rounded-xl border border-border bg-surface shadow-card">
    <summary className="min-h-11 cursor-pointer px-5 py-3 font-semibold text-text-heading">{tx("Teknik ayrıntılar")} <span className="text-sm font-normal text-text-muted">{tx("(güvenli varsayılanlar kullanılır; değiştirmeniz gerekmez)")}</span></summary>
    <div className="border-t border-border p-5"><FormGrid>{children}</FormGrid></div>
  </details>
}

/** A ready-made choice for a setting: exactly one option is pressed, or none when the current value is custom. */
export function PresetChoice({ label, options, disabled }: { label: string; options: readonly { text: string; pressed: boolean; onSelect: () => void }[]; disabled?: boolean }) {
  useLanguage()
  return <div role="group" aria-label={label} className="flex flex-wrap gap-2 md:col-span-2">
    {options.map(option => <Button key={option.text} type="button" variant={option.pressed ? 'default' : 'outline'} aria-pressed={option.pressed} disabled={disabled} onClick={option.onSelect}>{option.text}</Button>)}
  </div>
}
