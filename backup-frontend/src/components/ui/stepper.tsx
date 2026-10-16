import { Check } from 'lucide-react'
import { cn } from '@/lib/utils'

/** A progress indicator for a linear flow; steps before the current one are done. Not interactive: navigation lives in the flow's own buttons. */
export function Stepper({ steps, current }: { steps: readonly string[]; current: number }) {
  return <nav aria-label="Adımlar"><ol className="flex flex-wrap gap-x-2 gap-y-2">
    {steps.map((step, index) => {
      const state = index < current ? 'done' : index === current ? 'current' : 'todo'
      return <li key={step} aria-current={state === 'current' ? 'step' : undefined} className="flex items-center gap-2">
        <span className={cn('flex size-7 shrink-0 items-center justify-center rounded-full border text-sm font-semibold',
          state === 'done' && 'border-primary bg-primary text-primary-foreground', state === 'current' && 'border-primary bg-accent text-accent-foreground', state === 'todo' && 'border-border-strong/60 text-text-muted')}>
          {state === 'done' ? <Check aria-hidden="true" className="size-4" /> : index + 1}
        </span>
        <span className={cn('text-sm', state === 'current' ? 'font-semibold text-text-heading' : 'text-text-muted')}>{step}<span className="sr-only">{state === 'done' ? ' (tamamlandı)' : state === 'current' ? ' (geçerli adım)' : ''}</span></span>
        {index < steps.length - 1 && <span aria-hidden="true" className="mx-1 hidden h-px w-6 bg-border-strong/50 sm:block" />}
      </li>
    })}
  </ol></nav>
}
