import type { ConfigField, ConnectionConfiguration, ProviderDescriptor, SecretField, SecretValues, TenantCreateConnectionInput } from './types'

/** Text and Number stay as typed strings; Boolean is held as a checkbox value until submission. */
export type FieldValues = Record<string, string | boolean>

export function initialFieldValues(fields: readonly ConfigField[], configuration: ConnectionConfiguration = {}, providerType?: string): FieldValues {
  return Object.fromEntries(fields.map((field) => {
    const current = configuration[field.name] ?? (providerType === 'sftp' ? ({ port: '22', authenticationType: 'password' } as ConnectionConfiguration)[field.name] : undefined)
    if (field.kind === 'Boolean') return [field.name, current === 'true']
    return [field.name, current ?? '']
  }))
}

function typed(values: FieldValues, name: string) {
  const value = values[name]
  return typeof value === 'string' ? value.trim() : ''
}

/** The API takes a string dictionary, including Number and Boolean descriptors. Optional empty fields are omitted. */
export function buildConfiguration(fields: readonly ConfigField[], values: FieldValues): ConnectionConfiguration {
  const configuration: ConnectionConfiguration = {}
  for (const field of fields) {
    if (field.kind === 'Boolean') configuration[field.name] = String(values[field.name] === true)
    else if (typed(values, field.name) !== '') configuration[field.name] = typed(values, field.name)
  }
  return configuration
}

export function configurationComplete(fields: readonly ConfigField[], values: FieldValues): boolean {
  return fields.every((field) => !field.required || field.kind === 'Boolean' || typed(values, field.name) !== '')
}

/** Only non-empty values are sent: a blank secret input means "not provided", never an empty secret. */
export function buildSecretValues(fields: readonly SecretField[], values: Record<string, string>): SecretValues {
  const secret: SecretValues = {}
  for (const field of fields) if (values[field.name]) secret[field.name] = values[field.name]
  return secret
}

export function secretsComplete(fields: readonly SecretField[], values: Record<string, string>): boolean {
  return fields.every((field) => !field.required || Boolean(values[field.name]))
}

/** SFTP credentials follow the selected authentication method; hidden values are never submitted. */
export function activeSecretFields(providerType: string, configuration: FieldValues | ConnectionConfiguration, fields: readonly SecretField[]): SecretField[] {
  if (providerType === 'google-drive') return []
  if (providerType !== 'sftp') return [...fields]
  const keyAuthentication = configuration.authenticationType === 'private-key'
  return fields
    .filter((field) => keyAuthentication ? field.name === 'privateKey' || field.name === 'passphrase' : field.name === 'password')
    .map((field) => ({ ...field, required: field.name !== 'passphrase' }))
}

export function hasSecretInput(values: Record<string, string>): boolean {
  return Object.values(values).some(Boolean)
}
/** `configuration`/`secret` are omitted entirely when the provider declares no such fields or nothing was entered. */
export function providerPayload(provider: ProviderDescriptor, name: string, config: FieldValues, secret: Record<string, string>): TenantCreateConnectionInput {
  const input: TenantCreateConnectionInput = { providerType: provider.providerType, name: name.trim() }
  if (provider.configFields.length > 0) input.configuration = buildConfiguration(provider.configFields, config)
  const fields = activeSecretFields(provider.providerType, config, provider.secretFields)
  const credentials = buildSecretValues(fields, secret)
  if (hasSecretInput(credentials)) input.secret = credentials
  return input
}
