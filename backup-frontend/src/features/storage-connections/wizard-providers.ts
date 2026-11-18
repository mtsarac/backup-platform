import { tx } from '@/lib/i18n'
import type { FieldValues } from './descriptor-values'
import type { ProviderDescriptor } from './types'

/** The wizard's provider order: three main choices, then the rest under "Diğer depolama". Anything not listed (central, unknown types) follows S3 and SMB. */
export const mainProviders: readonly string[] = ['google-drive', 'sftp', 'local']
export const otherProviders: readonly string[] = ['s3', 'smb']

/** The wizard's plain name for a provider, and the config field that distinguishes one connection from another in the generated name. */
const names: Record<string, { title: () => string; prefix: () => string; key?: string }> = {
  'google-drive': { title: () => 'Google Drive', prefix: () => 'Google Drive' },
  sftp: { title: () => 'SFTP', prefix: () => 'SFTP', key: 'host' },
  local: { title: () => tx('Yerel disk'), prefix: () => tx('Yerel disk'), key: 'volume' },
  s3: { title: () => 'S3', prefix: () => 'S3', key: 'bucket' },
  smb: { title: () => 'SMB / NAS', prefix: () => 'NAS', key: 'share' },
}

export const providerTitle = (provider: ProviderDescriptor) => names[provider.providerType]?.title() ?? tx(provider.displayName)

/** "SFTP (sunucu)", "Yerel disk (D)", "NAS (paylaşım)", "S3 (bucket)"; Google Drive has no value to add until its account is connected. */
export function autoName(provider: ProviderDescriptor, config: FieldValues, account?: string): string {
  const entry = names[provider.providerType]
  const prefix = entry?.prefix() ?? tx(provider.displayName)
  const value = account ?? (entry?.key && typeof config[entry.key] === 'string' ? config[entry.key].toString().trim() : '')
  return value ? `${prefix} (${value})` : prefix
}

/** Fields the wizard never shows: every provider's `site`/`mediaClass`, S3 Object Lock / WORM / session token, SMB domain. Hidden fields stay empty and are not sent. */
const hidden: Record<string, { config: readonly string[]; secret: readonly string[] }> = {
  s3: { config: ['objectLockMode', 'retentionDays'], secret: ['sessionToken'] },
  smb: { config: [], secret: ['domain'] },
}

export function wizardProvider(provider: ProviderDescriptor): ProviderDescriptor {
  const extra = hidden[provider.providerType]
  return {
    ...provider,
    configFields: provider.configFields.filter((field) => !['site', 'mediaClass'].includes(field.name) && !extra?.config.includes(field.name)),
    secretFields: provider.secretFields.filter((field) => !extra?.secret.includes(field.name)),
  }
}

/** Where a wizard opened from a job draft sends the person back to: route state only, never the URL or storage. */
export interface WizardReturn { returnTo: string; returnState?: unknown }
export const wizardReturn = (state: unknown): WizardReturn | undefined =>
  typeof state === 'object' && state !== null && 'returnTo' in state && typeof state.returnTo === 'string' ? (state as WizardReturn) : undefined
