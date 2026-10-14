import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { EditConnectionDialog, SecretDialog } from './connection-dialogs'
import { useGoogleDriveConnect, useGoogleDriveDisconnect, type ConnectionOps } from './hooks'
import { openGoogleOAuthPopup } from './google-oauth'
import { connectionErrorMessage, disableEffects, enableEffect } from './labels'
import type { ConnectionView, ProviderDescriptor, SecretField } from './types'

type Dialog = 'edit' | 'secret' | 'clearSecret' | 'toggle' | 'delete' | 'disconnectGoogle'

/** The descriptor's secret fields, or the names the connection reports when the provider catalog is not loaded. */
function secretFieldsOf(connection: ConnectionView, descriptor?: ProviderDescriptor): readonly SecretField[] {
  return descriptor?.secretFields ?? connection.secretFieldNames.map((name) => ({ name, label: name, required: false }))
}

/**
 * Every button follows `connection.permissions` and nothing else: the API computed them for this caller on this route, so the same component
 * serves a tenant Admin, a PlatformAdmin and a read-only Viewer (who simply gets no buttons).
 */
export function ConnectionActions({ connection, descriptor, ops }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ConnectionOps }) {
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const { permissions } = connection
  const secretFields = secretFieldsOf(connection, descriptor)
  const testing = ops.test.isPending && ops.test.variables?.id === connection.id
  const testError = ops.test.error && ops.test.variables?.id === connection.id ? connectionErrorMessage(ops.test.error) : undefined
  const close = () => setDialog(null)
  const label = (text: string) => `${connection.name} bağlantısı: ${text}`
  // The platform controls only the lock; tenants control only their own preference.
  const toggledOn = ops.scope === 'platform' ? !connection.administrativelyDisabled : connection.enabled
  const toggleLabel = ops.scope === 'platform' ? (toggledOn ? 'Platform kilidi koy' : 'Platform kilidini kaldır') : (toggledOn ? 'Devre dışı bırak' : 'Etkinleştir')
  const cannotEnable = ops.scope === 'tenant' && connection.administrativelyDisabled && !connection.enabled

  function open(next: Dialog, reset: () => void) {
    reset()
    setDialog(next)
  }
  const connectGoogle = useGoogleDriveConnect()
  const disconnectGoogle = useGoogleDriveDisconnect()

  function startGoogleOAuth() {
    connectGoogle.mutate({ tenantId: connection.tenantId, connectionId: connection.id }, {
      onSuccess: ({ authorizationUrl }) => openGoogleOAuthPopup(authorizationUrl, () => ops.test.mutate(connection)),
    })
  }

  return <div className="space-y-1.5">
    <div className="flex flex-wrap gap-1.5">
      {(permissions.canEdit || permissions.canSetQuotaScope) && <Button variant="outline" size="sm" aria-label={label('düzenle')} onClick={() => open('edit', ops.update.reset)}>Düzenle</Button>}
      {permissions.canToggle && <Button variant="outline" size="sm" aria-label={label(toggleLabel.toLocaleLowerCase('tr-TR'))} disabled={cannotEnable} title={cannotEnable ? 'Platform kilidi kaldırılmadan etkinleştirilemez' : undefined} onClick={() => open('toggle', ops.setEnabled.reset)}>{toggleLabel}</Button>}
      {permissions.canTest && <Button variant="outline" size="sm" aria-label={label('test et')} disabled={testing} onClick={() => ops.test.mutate(connection)}>{testing ? 'Test ediliyor…' : 'Test et'}</Button>}
      {connection.providerType === 'google-drive' ? (
        permissions.canManageSecret && <>
          <Button variant="outline" size="sm" aria-label={label(connection.hasSecret ? 'yeniden bağla' : 'google hesabını bağla')} disabled={connectGoogle.isPending} onClick={startGoogleOAuth}>
            {connectGoogle.isPending ? 'Yönlendiriliyor…' : connection.hasSecret ? 'Yeniden bağla' : 'Google Hesabını Bağla'}
          </Button>
          {connection.hasSecret && <Button variant="outline" size="sm" aria-label={label('bağlantıyı kes')} onClick={() => open('disconnectGoogle', disconnectGoogle.reset)}>Bağlantıyı kes</Button>}
        </>
      ) : (
        <>
          {permissions.canManageSecret && secretFields.length > 0 && <Button variant="outline" size="sm" aria-label={label(connection.hasSecret ? 'kimlik bilgisini yenile' : 'kimlik bilgisi ayarla')} onClick={() => open('secret', ops.setSecret.reset)}>{connection.hasSecret ? 'Kimlik bilgisini yenile' : 'Kimlik bilgisi ayarla'}</Button>}
          {permissions.canManageSecret && connection.hasSecret && <Button variant="outline" size="sm" aria-label={label('kimlik bilgisini temizle')} onClick={() => open('clearSecret', ops.clearSecret.reset)}>Kimlik bilgisini temizle</Button>}
        </>
      )}
      {permissions.canDelete && <Button variant="destructive" size="sm" aria-label={label('sil')} onClick={() => open('delete', ops.remove.reset)}>Sil</Button>}
    </div>
    {testError && <p role="alert" className="text-xs text-error">{testError}</p>}

    {dialog === 'edit' && <EditConnectionDialog connection={connection} configFields={descriptor?.configFields ?? []} ops={ops} onClose={close} />}
    {dialog === 'secret' && <SecretDialog connection={connection} fields={secretFields} ops={ops} onClose={close} />}
    <ConfirmDialog open={dialog === 'clearSecret'} onClose={close} title={`“${connection.name}” kimlik bilgisi temizlensin mi?`} confirmLabel="Kimlik bilgisini temizle"
      description="Kayıtlı kimlik bilgisi silinir. Yeniden ayarlanana kadar bu hedefe erişim gerektiren işlemler başarısız olabilir."
      pending={ops.clearSecret.isPending} error={ops.clearSecret.error ? connectionErrorMessage(ops.clearSecret.error) : undefined}
      onConfirm={() => ops.clearSecret.mutate(connection, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'toggle'} onClose={close} title={ops.scope === 'platform' ? `“${connection.name}”: ${toggleLabel.toLocaleLowerCase('tr-TR')}?` : connection.enabled ? `“${connection.name}” devre dışı bırakılsın mı?` : `“${connection.name}” etkinleştirilsin mi?`}
      confirmLabel={toggleLabel}
      description={<>
        {ops.scope === 'platform' && <span className="block">Yalnızca platform kilidi değişir; sahibin etkin / devre dışı tercihi korunur.</span>}
        {toggledOn
          ? disableEffects.map((effect) => <span key={effect} className="mt-1 block">• {effect}</span>)
          : <span className="block">{ops.scope === 'platform' && !connection.enabled ? 'Kilit kalksa da sahip tercihi devre dışı olduğu için yeni backup başlatılamaz.' : enableEffect}</span>}
      </>}
      pending={ops.setEnabled.isPending} error={ops.setEnabled.error ? connectionErrorMessage(ops.setEnabled.error) : undefined}
      onConfirm={() => { if (!cannotEnable) ops.setEnabled.mutate({ connection, enabled: !toggledOn }, { onSuccess: close }) }} />
    <ConfirmDialog open={dialog === 'delete'} onClose={close} title={`“${connection.name}” silinsin mi?`} confirmLabel="Bağlantıyı sil"
      description="Bağlantı kalıcı olarak silinir. Replica veya Backup Job hâlâ bu bağlantıyı kullanıyorsa silinemez; önce bunların başka bir hedefe taşınması gerekir."
      pending={ops.remove.isPending} error={ops.remove.error ? connectionErrorMessage(ops.remove.error) : undefined}
      onConfirm={() => ops.remove.mutate(connection, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'disconnectGoogle'} onClose={close} title={`“${connection.name}” Google bağlantısı kesilsin mi?`} confirmLabel="Bağlantıyı kes"
      description="Kayıtlı Google erişim izni iptal edilir. Aynı Google hesabını ve uygulamayı kullanan diğer bağlantıların da erişimi iptal olabilir; bunlar yeniden yetkilendirilmelidir. Yeniden yetkilendirilene kadar bu hedefe erişim gerektiren işlemler başarısız olabilir."
      pending={disconnectGoogle.isPending} error={disconnectGoogle.error ? connectionErrorMessage(disconnectGoogle.error) : undefined}
      onConfirm={() => disconnectGoogle.mutate({ tenantId: connection.tenantId, connectionId: connection.id }, { onSuccess: close })} />
  </div>
}
