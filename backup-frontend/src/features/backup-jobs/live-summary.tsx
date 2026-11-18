import { tx, useLanguage } from '@/lib/i18n'
import { Card } from '@/components/ui/card'
import { Disclosure } from '@/components/ui/disclosure'
import { useLargeScreen } from '@/hooks/use-large-screen'
import { summaryInput, summaryRows } from '@/features/backup-jobs/job-summary-rows'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'

/** What the job will be, updated as the form is filled in, with the wizard's position. Beside the form on a wide screen, an expandable block on a narrow one. */
export function LiveSummary({ form, position, label }: { form: JobFormState; position: string; label: string }) {
  useLanguage()
  const large = useLargeScreen()
  const body = <>
    <p className="text-sm text-text-muted">{tx("Adım")} {position}: {label}</p>
    <dl className="mt-3 space-y-3 text-sm">
      {summaryRows(summaryInput(form)).map(({ label: term, value }) => <div key={term}><dt className="text-text-muted">{term}</dt><dd className="mt-0.5 break-words text-text">{value}</dd></div>)}
    </dl>
  </>
  return <aside aria-label={tx("Canlı özet")} className="max-lg:order-first lg:sticky lg:top-4 lg:self-start">
    {large ? <Card><h3 className="mb-1 font-medium text-text-heading">{tx("Özet")}</h3>{body}</Card> : <Disclosure title={tx("Özet")}>{body}</Disclosure>}
  </aside>
}
