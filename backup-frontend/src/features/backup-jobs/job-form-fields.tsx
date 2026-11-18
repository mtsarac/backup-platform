import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'

export function FormGrid({ children }: { children: ReactNode }) {
  useLanguage()
  return <div className="grid gap-4 md:grid-cols-2 [&>div]:min-w-0">{children}</div>
}

/** One wizard question with the short reason it is asked. */
export function Question({ title, why, children, headingLevel, compact }: { title: string; why: string; children?: ReactNode; headingLevel?: 2 | 3; compact?: boolean }) {
  useLanguage()
  return <Section title={title} headingLevel={headingLevel} className={compact ? '[&>div:first-child]:border-0 [&>div:first-child]:pb-0 [&>div:nth-child(2)]:pt-2' : undefined} description={compact ? undefined : <><span className="font-medium text-text-heading">{tx("Neden soruluyor?")}</span>{' '}{why}</>}>{children}</Section>
}

/** A ready-made choice for a setting: exactly one option is pressed, or none when the current value is custom. */
export function PresetChoice({ label, options, disabled }: { label: string; options: readonly { text: string; pressed: boolean; onSelect: () => void }[]; disabled?: boolean }) {
  useLanguage()
  return <div role="group" aria-label={label} className="flex flex-wrap gap-2 md:col-span-2">
    {options.map(option => <Button key={option.text} type="button" variant={option.pressed ? 'default' : 'outline'} aria-pressed={option.pressed} disabled={disabled} onClick={option.onSelect}>{option.text}</Button>)}
  </div>
}
