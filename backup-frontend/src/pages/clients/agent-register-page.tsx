import { tx } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { QueryState } from '@/components/data/query-state'
import { DataTable, type Column } from '@/components/data/data-table'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { AgentsTabs } from '@/features/agents/agents-tabs'
import { linkClass } from '@/features/platform/styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { agentQueryKeys, useEnrollmentPolicy, useEnrollmentTokens } from '@/features/agents/hooks'
import { revokeEnrollmentToken, setEnrollmentPolicy } from '@/features/agents/api'
import type { EnrollmentToken } from '@/features/agents/types'
import { agentDate } from '@/features/agents/format'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { AddAgentDialog } from '@/features/agents/add-agent-dialog'
import { errorMessage } from '@/lib/api/failure-messages'
import { useTranslation } from '@/lib/i18n'

export function AgentRegisterPage() {
  const { tenantId, canAdmin } = useTenant()
  const { t } = useTranslation()
  return canAdmin ? <EnrollmentContent key={tenantId} tenantId={tenantId} /> : <div className="space-y-6">
    <PageHeader title={t('agents.enrollmentTitle')} description={t('agents.enrollmentDescription')} />
    <Notice tone="error" title={tx("Yetkiniz yok")} action={<Link className={linkClass} to={`/tenants/${tenantId}/agents`}>{tx("Agent'lara dön")}</Link>}>{tx("Agent kaydını yönetmek için çalışma alanı yöneticisi olmalısınız.")}</Notice>
  </div>
}

function EnrollmentContent({ tenantId }: { tenantId: string }) {
  const { t } = useTranslation()
  const policy = useEnrollmentPolicy(tenantId, true)
  const tokens = useEnrollmentTokens(tenantId, true)
  const client = useQueryClient()
  const [createOpen, setCreateOpen] = useState(false)
  const [selected, setSelected] = useState<EnrollmentToken | null>(null)
  const changePolicy = useMutation({ mutationFn: (enabled: boolean) => setEnrollmentPolicy(tenantId, enabled), onSuccess: async () => {
    setCreateOpen(false)
    await Promise.all([
      client.invalidateQueries({ queryKey: agentQueryKeys.policy(tenantId) }),
      client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
    ])
  } })
  const revoke = useMutation({ mutationFn: (tokenId: string) => revokeEnrollmentToken(tenantId, tokenId), onSuccess: async () => {
    setSelected(null)
    await Promise.all([
      client.invalidateQueries({ queryKey: agentQueryKeys.tokens(tenantId) }),
      client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
    ])
  } })
  const columns: Column<EnrollmentToken>[] = [
    { key: 'createdAt', header: tx('Oluşturulma'), render: (token) => agentDate(token.createdAt) },
    { key: 'expiresAt', header: tx('Son kullanma'), render: (token) => agentDate(token.expiresAt) },
    { key: 'actions', header: tx('İşlemler'), render: (token) => <Button variant="destructive" aria-label={tx("{0} tarihinde oluşturulan token'ı iptal et", { "0": agentDate(token.createdAt) })} onClick={() => { revoke.reset(); setSelected(token) }}>{tx("İptal et")}</Button> },
  ]
  return <div className="space-y-6">
    <PageHeader title={t('agents.enrollmentTitle')} description={t('agents.enrollmentDescription')} />
    <AgentsTabs />
    <QueryState isPending={policy.isPending} error={policy.error}>
      {policy.data && <Section title={tx("Yeni kayıt izni")} description={policy.data.enabled ? tx('Agent kaydı açık.') : tx('Agent kaydı kapalı.')} actions={<Button role="switch" aria-checked={policy.data.enabled} aria-label={tx("Yeni agent kayıtlarına izin ver")} variant="outline" disabled={changePolicy.isPending} onClick={() => changePolicy.mutate(!policy.data!.enabled)}>{changePolicy.isPending ? tx('Kaydediliyor…') : policy.data.enabled ? tx('Kaydı kapat') : tx('Kaydı aç')}</Button>}>
        <p className="text-sm text-text-muted">{tx("Kaydı kapatmak mevcut agent'ları iptal etmez; yeni kayıtları engeller.")}</p>
        {changePolicy.error && <Notice tone="error" className="mt-3">{errorMessage(changePolicy.error)}</Notice>}
      </Section>}
    </QueryState>
    <Section title={t('agents.activeTokensTitle')} description={t('agents.activeTokensDesc')} flush actions={<Button disabled={!policy.data?.enabled || Boolean(policy.error) || changePolicy.isPending} onClick={() => setCreateOpen(true)}>{t('agents.createToken')}</Button>}>
      <QueryState isPending={tokens.isPending} error={tokens.error}><DataTable rows={tokens.data ?? []} columns={columns} keyExtractor={(token) => token.tokenId} emptyMessage={tx("Aktif kayıt token'ı yok.")} className="rounded-none border-0 shadow-none" /></QueryState>
    </Section>
    {createOpen && policy.data?.enabled && !changePolicy.isPending && <AddAgentDialog tenantId={tenantId} onClose={() => setCreateOpen(false)} />}
    <ConfirmDialog open={Boolean(selected)} onClose={() => setSelected(null)} title={tx("Kayıt token'ı iptal edilsin mi?")} description={tx("Bu token yeni agent kaydı için kullanılamayacak. Mevcut agent'lar etkilenmez.")} pending={revoke.isPending} error={revoke.error ? errorMessage(revoke.error) : undefined} onConfirm={() => { if (selected) revoke.mutate(selected.tokenId) }} />
  </div>
}
