import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { EditConnectionDialog, SecretDialog } from './connection-dialogs'
import { useGoogleDriveConnect, useGoogleDriveDisconnect, type ConnectionOps } from './hooks'
import { openGoogleOAuthPopup } from './google-oauth'
import { disableEffects, enableEffect } from './labels'
import type { ConnectionView, ProviderDescriptor, SecretField } from './types'
import { errorMessage } from '@/lib/api/failure-messages'

export type ActionGroup = 'edit' | 'test' | 'credentials' | 'toggle' | 'delete'
type Dialog = 'edit' | 'secret' | 'clearSecret' | 'toggle' | 'delete' | 'disconnectGoogle'

/** The descriptor's secret fields, or the names the connection reports when the provider catalog is not loaded. */
function secretFieldsOf(connection: ConnectionView, descriptor?: ProviderDescriptor): readonly SecretField[] {
  return descriptor?.secretFields ?? connection.secretFieldNames.map((name) => ({ name, label: name, required: false }))
}

/**
 * Every button follows `connection.permissions` and nothing else: the API computed them for this caller on this route, so the same component
 * serves a tenant Admin, a PlatformAdmin and a read-only Viewer (who simply gets no buttons).
 */
export function ConnectionActions({ connection, descriptor, ops, show = ['edit', 'test', 'credentials', 'toggle', 'delete'] }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ConnectionOps; show?: readonly ActionGroup[] }) {
  useLanguage()
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const { permissions } = connection
  const secretFields = secretFieldsOf(connection, descriptor)
  const testing = ops.test.isPending && ops.test.variables?.id === connection.id
  const testError = ops.test.error && ops.test.variables?.id === connection.id ? errorMessage(ops.test.error, 'storageConnection') : undefined
  const close = () => setDialog(null)
  const label = (text: string) => tx("{0} bağlantısı: {1}", { "0": connection.name, "1": text })
  // The platform controls only the lock; tenants control only their own preference.
  const toggledOn = ops.scope === 'platform' ? !connection.administrativelyDisabled : connection.enabled
  const toggleLabel = ops.scope === 'platform' ? (toggledOn ? tx('Platform kilidi koy') : tx('Platform kilidini kaldır')) : (toggledOn ? tx('Devre dışı bırak') : tx('Etkinleştir'))
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

  return <div className="space-y-2">
    <div className="flex flex-wrap gap-2">
      {show.includes('edit') && (permissions.canEdit || permissions.canSetQuotaScope) && <Button variant="outline" size="sm" aria-label={label(tx('düzenle'))} onClick={() => open('edit', ops.update.reset)}>{tx("Düzenle")}</Button>}
      {show.includes('toggle') && permissions.canToggle && <Button variant="outline" size="sm" aria-label={label(toggleLabel.toLocaleLowerCase(currentLocale()))} disabled={cannotEnable} title={cannotEnable ? tx('Platform kilidi kaldırılmadan etkinleştirilemez') : undefined} onClick={() => open('toggle', ops.setEnabled.reset)}>{toggleLabel}</Button>}
      {show.includes('test') && permissions.canTest && <Button variant="outline" size="sm" aria-label={label(tx('test et'))} disabled={testing} onClick={() => ops.test.mutate(connection)}>{testing ? tx('Test ediliyor…') : tx('Test et')}</Button>}
      {!show.includes('credentials') ? null : connection.providerType === 'google-drive' ? (
        permissions.canManageSecret && <>
          <Button variant="outline" size="sm" aria-label={label(connection.hasSecret ? tx('yeniden bağla') : tx('google hesabını bağla'))} disabled={connectGoogle.isPending} onClick={startGoogleOAuth}>
            {connectGoogle.isPending ? tx('Yönlendiriliyor…') : connection.hasSecret ? tx('Yeniden bağla') : tx('Google Hesabını Bağla')}
          </Button>
          {connection.hasSecret && <Button variant="outline" size="sm" aria-label={label(tx('bağlantıyı kes'))} onClick={() => open('disconnectGoogle', disconnectGoogle.reset)}>{tx("Bağlantıyı kes")}</Button>}
        </>
      ) : (
        <>
          {permissions.canManageSecret && secretFields.length > 0 && <Button variant="outline" size="sm" aria-label={label(connection.hasSecret ? tx('kimlik bilgisini yenile') : tx('kimlik bilgisi ayarla'))} onClick={() => open('secret', ops.setSecret.reset)}>{connection.hasSecret ? tx('Kimlik bilgisini yenile') : tx('Kimlik bilgisi ayarla')}</Button>}
          {permissions.canManageSecret && connection.hasSecret && <Button variant="outline" size="sm" aria-label={label(tx('kimlik bilgisini temizle'))} onClick={() => open('clearSecret', ops.clearSecret.reset)}>{tx("Kimlik bilgisini temizle")}</Button>}
        </>
      )}
      {show.includes('delete') && permissions.canDelete && <Button variant="destructive" size="sm" aria-label={label('sil')} onClick={() => open('delete', ops.remove.reset)}>{tx("Sil")}</Button>}
    </div>
    {show.includes('test') && testError && <p role="alert" className="text-sm text-error">{testError}</p>}
    {show.includes('credentials') && Boolean(connectGoogle.error) && <p role="alert" className="text-sm text-error">{errorMessage(connectGoogle.error, 'storageConnection')}</p>}

    {dialog === 'edit' && <EditConnectionDialog connection={connection} configFields={descriptor?.configFields ?? []} ops={ops} onClose={close} />}
    {dialog === 'secret' && <SecretDialog connection={connection} fields={secretFields} ops={ops} onClose={close} />}
    <ConfirmDialog open={dialog === 'clearSecret'} onClose={close} title={tx("“{0}” kimlik bilgisi temizlensin mi?", { "0": connection.name })} confirmLabel={tx("Kimlik bilgisini temizle")}
      consequence={{
        what: tx("Kayıtlı kimlik bilgisi silinir. Yeniden ayarlanana kadar bu depolamaya erişim gerektiren işlemler başarısız olabilir."),
        who: tx("Bu depolamayı kullanan yedekleme işleri ve geri yüklemeler etkilenir."),
        reversible: tx("Evet. Yeni bir kimlik bilgisi ayarlayabilirsiniz; silinen değer geri gelmez."),
      }}
      pending={ops.clearSecret.isPending} error={ops.clearSecret.error} errorScope="storageConnection"
      onConfirm={() => ops.clearSecret.mutate(connection, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'toggle'} onClose={close} title={ops.scope === 'platform' ? `“${connection.name}”: ${toggleLabel.toLocaleLowerCase(currentLocale())}?` : connection.enabled ? tx("“{0}” devre dışı bırakılsın mı?", { "0": connection.name }) : tx("“{0}” etkinleştirilsin mi?", { "0": connection.name })}
      confirmLabel={toggleLabel}
      consequence={{
        what: <>
          {ops.scope === 'platform' && <span className="block">{tx("Yalnızca platform kilidi değişir; sahibin etkin / devre dışı tercihi korunur.")}</span>}
          {toggledOn
            ? disableEffects.map((effect) => <span key={effect} className="mt-1 block">• {tx(effect)}</span>)
            : <span className="block">{ops.scope === 'platform' && !connection.enabled ? tx('Kilit kalksa da sahip tercihi devre dışı olduğu için yeni backup başlatılamaz.') : tx(enableEffect)}</span>}
        </>,
        who: tx("Bu depolamayı kullanan yedekleme işleri etkilenir."),
        reversible: tx("Evet. Bu ayarı istediğiniz zaman geri değiştirebilirsiniz."),
      }}
      pending={ops.setEnabled.isPending} error={ops.setEnabled.error} errorScope="storageConnection"
      onConfirm={() => { if (!cannotEnable) ops.setEnabled.mutate({ connection, enabled: !toggledOn }, { onSuccess: close }) }} />
    <ConfirmDialog open={dialog === 'delete'} onClose={close} title={tx("“{0}” silinsin mi?", { "0": connection.name })} confirmLabel={tx("Bağlantıyı sil")}
      consequence={{
        what: tx("Bağlantı kalıcı olarak silinir. Bir yedek kopyası veya yedekleme işi hâlâ bu bağlantıyı kullanıyorsa silinemez; önce bunların başka bir depolamaya taşınması gerekir."),
        who: tx("Bu bağlantıyı kullanan yedekleme işleri ve kopyalar etkilenir."),
        reversible: tx("Hayır. Bağlantıyı yeniden eklemeniz gerekir."),
      }}
      pending={ops.remove.isPending} error={ops.remove.error} errorScope="storageConnection"
      onConfirm={() => ops.remove.mutate(connection, { onSuccess: close })} />
    <ConfirmDialog open={dialog === 'disconnectGoogle'} onClose={close} title={tx("“{0}” Google bağlantısı kesilsin mi?", { "0": connection.name })} confirmLabel={tx("Bağlantıyı kes")}
      consequence={{
        what: tx("Kayıtlı Google erişim izni iptal edilir. Yeniden yetkilendirilene kadar bu depolamaya erişim gerektiren işlemler başarısız olabilir."),
        who: tx("Aynı Google hesabını ve uygulamayı kullanan diğer bağlantıların da erişimi iptal olabilir; bunların yeniden yetkilendirilmesi gerekir."),
        reversible: tx("Evet. Google hesabını yeniden bağlayarak erişimi geri verebilirsiniz."),
      }}
      pending={disconnectGoogle.isPending} error={disconnectGoogle.error} errorScope="storageConnection"
      onConfirm={() => disconnectGoogle.mutate({ tenantId: connection.tenantId, connectionId: connection.id }, { onSuccess: close })} />
  </div>
}
