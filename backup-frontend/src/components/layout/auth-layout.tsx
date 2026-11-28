import type { ReactNode } from 'react'
import { BrandMark } from '@/components/ui/brand-mark'
import { LanguageSwitcher } from '@/components/layout/language-switcher'
import { ThemeToggle } from '@/components/layout/theme-toggle'

/** Shared frame of the public auth pages: top-aligned at one fixed offset so the logo never jumps between pages, with theme and language controls. */
export function AuthLayout({ title, children }: { title: string; children: ReactNode }) {
  return <div className="min-h-svh bg-canvas">
    <div className="mx-auto flex w-full max-w-sm items-center justify-end gap-2 px-4 pt-4">
      <LanguageSwitcher />
      <ThemeToggle />
    </div>
    <main className="mx-auto w-full max-w-sm space-y-6 px-4 pb-10 pt-8 sm:pt-16">
      <BrandMark size="sm" />
      <h1 className="text-xl font-semibold text-text-heading">{title}</h1>
      {children}
    </main>
  </div>
}
