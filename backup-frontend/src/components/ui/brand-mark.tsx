import { DatabaseBackup } from 'lucide-react'
import { tx, useLanguage } from '@/lib/i18n'
import { cn } from '@/lib/utils'

/** The product mark and name. `inverse` is for the dark sidebar and brand panels. */
export function BrandMark({ size = 'md', inverse, className }: { size?: 'sm' | 'md'; inverse?: boolean; className?: string }) {
  useLanguage()
  return <div className={cn('flex items-center gap-3', className)}>
    <span className={cn('flex items-center justify-center rounded-lg bg-primary text-primary-foreground', size === 'md' ? 'size-10' : 'size-9')}>
      <DatabaseBackup aria-hidden="true" className={size === 'md' ? 'size-6' : 'size-5'} />
    </span>
    <span className={cn('font-semibold', size === 'md' && 'text-lg', inverse ? 'text-sidebar-foreground' : 'text-text-heading')}>{tx("Backup Platform")}</span>
  </div>
}
