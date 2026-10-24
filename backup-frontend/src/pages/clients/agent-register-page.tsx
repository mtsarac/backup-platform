import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { QueryState } from '@/components/data/query-state'
import { DataList, type Column } from '@/components/data/data-list'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { AgentsTabs } from '@/features/agents/agents-tabs'
import { linkClass } from '@/components/ui/form-styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { agentQueryKeys, useEnrollmentPolicy, useEnrollmentTokens } from '@/features/agents/hooks'
import { revokeEnrollmentToken, setEnrollmentPolicy } from '@/features/agents/api'
import type { EnrollmentToken } from '@/features/agents/types'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { AddAgentDialog } from '@/features/agents/add-agent-dialog'
import { AccessDenied } from '@/components/auth/access-denied'
import { formatDateTime } from '@/lib/format'

export function AgentRegisterPage() {
  const { tenantId, canAdmin } = useTenant()
  useLanguage()
  return canAdmin ? <EnrollmentContent key={tenantId} tenantId={tenantId} /> : <div className="space-y-6">
    <PageHeader title={tx("Ajan Kaydı")} description={tx("Bu çalışma alanındaki yeni ajan kayıtlarını ve kurulum komutlarını yönetin.")} />
    <AccessDenied title={tx("Yetkiniz yok")} what={tx("Agent kaydını yönetmek için çalışma alanı yöneticisi olmalısınız.")} control={<Link className={linkClass} to={`/tenants/${tenantId}/agents`}>{tx("Agent'lara dön")}</Link>} />
  </div>
}

function EnrollmentContent({ tenantId }: { tenantId: string }) {
  useLanguage()
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
    { key: 'createdAt', header: tx('Oluşturulma'), render: (token) => formatDateTime(token.createdAt, tx('Henüz bildirilmedi')) },
    { key: 'expiresAt', header: tx('Son kullanma'), render: (token) => formatDateTime(token.expiresAt, tx('Henüz bildirilmedi')) },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), render: (token) => <Button variant="destructive" aria-label={tx("{0} tarihinde oluşturulan token'ı iptal et", { "0": formatDateTime(token.createdAt, tx('Henüz bildirilmedi')) })} onClick={() => { revoke.reset(); setSelected(token) }}>{tx("İptal et")}</Button> },
  ]
  return <div className="space-y-6">
    <PageHeader title={tx("Ajan Kaydı")} description={tx("Bu çalışma alanındaki yeni ajan kayıtlarını ve kurulum komutlarını yönetin.")} />
    <AgentsTabs />
    <QueryState isPending={policy.isPending} error={policy.error}>
      {policy.data && <Section title={tx("Yeni kayıt izni")} description={policy.data.enabled ? tx('Agent kaydı açık.') : tx('Agent kaydı kapalı.')} actions={<Button role="switch" aria-checked={policy.data.enabled} aria-label={tx("Yeni agent kayıtlarına izin ver")} variant="outline" disabled={changePolicy.isPending} onClick={() => changePolicy.mutate(!policy.data!.enabled)}>{changePolicy.isPending ? tx('Kaydediliyor…') : policy.data.enabled ? tx('Kaydı kapat') : tx('Kaydı aç')}</Button>}>
        <p className="text-sm text-text-muted">{tx("Kaydı kapatmak mevcut agent'ları iptal etmez; yeni kayıtları engeller.")}</p>
        {changePolicy.error && <ErrorNotice className="mt-3" error={changePolicy.error} />}
      </Section>}
    </QueryState>
    <Section title={tx("Etkin Kayıt Anahtarları")} description={tx("Kayıt anahtarı değeri güvenlik nedeniyle listelenmez. Süresi dolan, kullanılmış veya iptal edilmiş kayıt anahtarları gösterilmez.")} flush actions={<Button disabled={!policy.data?.enabled || Boolean(policy.error) || changePolicy.isPending} onClick={() => setCreateOpen(true)}>{tx("Kayıt anahtarı oluştur")}</Button>}>
      <QueryState isPending={tokens.isPending} error={tokens.error}><DataList rows={tokens.data ?? []} columns={columns} keyExtractor={(token) => token.tokenId} emptyMessage={tx("Aktif kayıt token'ı yok.")} className="rounded-none border-0 shadow-none" /></QueryState>
    </Section>
    {createOpen && policy.data?.enabled && !changePolicy.isPending && <AddAgentDialog tenantId={tenantId} onClose={() => setCreateOpen(false)} />}
    <ConfirmDialog open={Boolean(selected)} onClose={() => setSelected(null)} title={tx("Kayıt anahtarı iptal edilsin mi?")}
      consequence={{
        what: tx("Bu anahtar yeni bilgisayar kaydı için kullanılamaz."),
        who: tx("Yalnızca bu anahtarla henüz kaydolmamış bilgisayarlar etkilenir; mevcut yedekleme bilgisayarları etkilenmez."),
        reversible: tx("Hayır. Yeni bir kayıt anahtarı oluşturmanız gerekir."),
      }}
      confirmLabel={tx("İptali onayla")} pending={revoke.isPending} error={revoke.error} onConfirm={() => { if (selected) revoke.mutate(selected.tokenId) }} />
  </div>
}
