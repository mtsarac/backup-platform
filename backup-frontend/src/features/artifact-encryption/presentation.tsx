import { tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'
import { Fact } from '@/components/ui/section'
import { breakGlassStatusLabels, formatLabels, policyShortLabels, recipientLabels, wrapKeyStateLabels } from './labels'
import type { ArtifactEncryption, BreakGlassStatus, EncryptionPolicy, WrapKeyState } from './types'

export function PolicyBadge({ policy }: { policy: EncryptionPolicy | undefined }) {
  useLanguage()
  const value = policy ?? 'Off'
  return <Badge tone={value === 'Off' ? 'neutral' : 'success'}>{policyShortLabels[value]}</Badge>
}
export function WrapKeyStateBadge({ state }: { state: WrapKeyState }) {
  useLanguage()
  const meta = wrapKeyStateLabels[state]
  return <Badge dot tone={meta.tone}>{meta.label}</Badge>
}
export function BreakGlassStatusBadge({ status }: { status: BreakGlassStatus }) {
  useLanguage()
  const meta = breakGlassStatusLabels[status]
  return <Badge dot tone={meta.tone}>{meta.label}</Badge>
}

/** Short key ids are enough to match a key against the ceremony log; the full id stays in the title. */
export function KeyId({ value }: { value: string }) {
  return <code title={value} className="font-mono text-sm">{value.slice(0, 12)}…</code>
}

/** Facts of an artifact's encryption, shared by the tenant artifact detail and the platform support projection. */
export function ArtifactEncryptionFacts({ encryption }: { encryption: ArtifactEncryption | null | undefined }) {
  useLanguage()
  if (!encryption) return <Fact label={tx("Şifreleme")}>{formatLabels['v1-plain']}</Fact>
  const platformRecovery = encryption.recipients.some(recipient => recipient.type === 'platform-recovery')
  return <>
    <Fact label={tx("Şifreleme")}>{formatLabels[encryption.format]}</Fact>
    <Fact label={tx("Anahtar sürümü")}>{encryption.keyVersion} · <KeyId value={encryption.keyId} /></Fact>
    <Fact label={tx("Platform kurtarma alıcısı")}>{platformRecovery ? tx('Var') : tx('Yok')}</Fact>
    <Fact label={tx("Alıcılar")}><ul className="space-y-0.5">{encryption.recipients.map(recipient => <li key={`${recipient.type}:${recipient.keyId}`}>{recipientLabels[recipient.type]} · v{recipient.keyVersion} · <KeyId value={recipient.keyId} /></li>)}</ul></Fact>
  </>
}
