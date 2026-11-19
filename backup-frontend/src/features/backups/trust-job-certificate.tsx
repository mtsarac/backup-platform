import type { ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { ErrorNotice } from '@/components/ui/notice'
import { useTrustJobCertificate } from '@/features/backup-jobs/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { tx, useLanguage } from '@/lib/i18n'

/** Gives a failed run's error panel its "trust anyway" step: the job keeps the trust for good, and the run starts again on its new attempt page. */
export function TrustJobCertificate({ jobId, children }: { jobId: string; children: (trust: { note: string; onConfirm: () => void; pending: boolean }) => ReactNode }) {
  useLanguage()
  const { tenantId } = useTenant()
  const navigate = useNavigate()
  const trust = useTrustJobCertificate()
  return <div className="space-y-3">
    {children({ note: tx("Bu işte sunucu sertifikası kalıcı olarak güvenilir sayılacak."), pending: trust.isPending, onConfirm: () => trust.mutate(jobId, { onSuccess: run => navigate(`/tenants/${tenantId}/backup-runs/${run.id}`) }) })}
    {trust.error && <ErrorNotice error={trust.error} />}
  </div>
}
