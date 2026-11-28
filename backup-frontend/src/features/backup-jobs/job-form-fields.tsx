import { tx, useLanguage } from '@/lib/i18n'
import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Section } from '@/components/ui/section'

export function FormGrid({ children }: { children: ReactNode }) {
  useLanguage()
  return <div className="grid gap-4 md:grid-cols-2 [&>div]:min-w-0">{children}</div>
}

/** The "Neden soruluyor?" label plus the reason; `lead={false}` drops the label on later questions of the same step. */
export function Why({ children, lead = true }: { children: ReactNode; lead?: boolean }) {
  useLanguage()
  return <>{lead && <><span className="font-medium text-text-heading">{tx("Neden soruluyor?")}</span>{' '}</>}{children}</>
}

/** One wizard question with the short reason it is asked. */
export function Question({ title, why, children, headingLevel, compact, lead = true }: { title: string; why: string; children?: ReactNode; headingLevel?: 2 | 3; compact?: boolean; lead?: boolean }) {
  useLanguage()
  return <Section title={title} headingLevel={headingLevel} className={compact ? '[&>div:first-child]:border-0 [&>div:first-child]:pb-0 [&>div:nth-child(2)]:pt-2' : undefined} description={compact ? undefined : <Why lead={lead}>{why}</Why>}>{children}</Section>
}

/** A ready-made choice for a setting: exactly one option is pressed, or none when the current value is custom. */
export function PresetChoice({ label, options }: { label: string; options: readonly { text: string; pressed: boolean; onSelect: () => void }[]}) {
  useLanguage()
  return <div role="group" aria-label={label} className="flex flex-wrap gap-2 md:col-span-2">
    {options.map(option => <Button key={option.text} type="button" variant={option.pressed ? 'default' : 'outline'} aria-pressed={option.pressed} onClick={option.onSelect}>{option.text}</Button>)}
  </div>
}
