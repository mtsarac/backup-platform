import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** The one action order for every step flow: Back on the left; on the right Cancel (quiet) and then the primary action. */
export function WizardFooter({ back, cancel, className, children }: { back?: ReactNode; cancel?: ReactNode; className?: string; children: ReactNode }) {
  return <div className={cn('flex flex-wrap items-center justify-between gap-3', className)}>
    <div>{back}</div>
    <div className="ml-auto flex flex-wrap items-center justify-end gap-3">{cancel}{children}</div>
  </div>
}
