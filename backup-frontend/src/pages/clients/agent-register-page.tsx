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
import { CreateTokenDialog } from '@/features/agents/create-token-dialog'
import { errorMessage } from '@/lib/api/failure-messages'

export function AgentRegisterPage() {
  const { tenantId, canAdmin } = useTenant()
  return canAdmin ? <EnrollmentContent key={tenantId} tenantId={tenantId} /> : <div className="space-y-6">
    <PageHeader title="Agent kaydı" description="Yeni Agent kayıtlarını ve kayıt token'larını yönetin." />
    <Notice tone="error" title="Yetkiniz yok" action={<Link className={linkClass} to={`/tenants/${tenantId}/agents`}>Agent'lara dön</Link>}>Agent kaydını yönetmek için çalışma alanı yöneticisi olmalısınız.</Notice>
  </div>
}

function EnrollmentContent({ tenantId }: { tenantId: string }) {
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
    { key: 'createdAt', header: 'Oluşturulma', render: (token) => agentDate(token.createdAt) },
    { key: 'expiresAt', header: 'Son kullanma', render: (token) => agentDate(token.expiresAt) },
    { key: 'actions', header: 'İşlemler', render: (token) => <Button variant="destructive" aria-label={`${agentDate(token.createdAt)} tarihinde oluşturulan token'ı iptal et`} onClick={() => { revoke.reset(); setSelected(token) }}>İptal et</Button> },
  ]
  return <div className="space-y-6">
    <PageHeader title="Agent kaydı" description="Bu çalışma alanındaki yeni agent kayıtlarını ve kayıt token'larını yönetin." />
    <AgentsTabs />
    <QueryState isPending={policy.isPending} error={policy.error}>
      {policy.data && <Section title="Yeni kayıt izni" description={policy.data.enabled ? 'Agent kaydı açık.' : 'Agent kaydı kapalı.'} actions={<Button role="switch" aria-checked={policy.data.enabled} aria-label="Yeni agent kayıtlarına izin ver" variant="outline" disabled={changePolicy.isPending} onClick={() => changePolicy.mutate(!policy.data!.enabled)}>{changePolicy.isPending ? 'Kaydediliyor…' : policy.data.enabled ? 'Kaydı kapat' : 'Kaydı aç'}</Button>}>
        <p className="text-sm text-text-muted">Kaydı kapatmak mevcut agent'ları iptal etmez; yeni kayıtları engeller.</p>
        {changePolicy.error && <Notice tone="error" className="mt-3">{errorMessage(changePolicy.error)}</Notice>}
      </Section>}
    </QueryState>
    <Section title="Aktif kayıt token'ları" description="Token değeri listelenmez. Süresi dolan, kullanılmış veya iptal edilmiş token'lar gösterilmez." flush actions={<Button disabled={!policy.data?.enabled || Boolean(policy.error) || changePolicy.isPending} onClick={() => setCreateOpen(true)}>Token oluştur</Button>}>
      <QueryState isPending={tokens.isPending} error={tokens.error}><DataTable rows={tokens.data ?? []} columns={columns} keyExtractor={(token) => token.tokenId} emptyMessage="Aktif kayıt token'ı yok." className="rounded-none border-0 shadow-none" /></QueryState>
    </Section>
    {createOpen && policy.data?.enabled && !changePolicy.isPending && <CreateTokenDialog tenantId={tenantId} onClose={() => setCreateOpen(false)} onCreated={() => { void client.invalidateQueries({ queryKey: agentQueryKeys.tokens(tenantId) }); void client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }) }} />}
    <ConfirmDialog open={Boolean(selected)} onClose={() => setSelected(null)} title="Kayıt token'ı iptal edilsin mi?" description="Bu token yeni agent kaydı için kullanılamayacak. Mevcut agent'lar etkilenmez." pending={revoke.isPending} error={revoke.error ? errorMessage(revoke.error) : undefined} onConfirm={() => { if (selected) revoke.mutate(selected.tokenId) }} />
  </div>
}
