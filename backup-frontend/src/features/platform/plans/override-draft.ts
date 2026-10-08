import { gbErrorMessage, parseGb } from './storage'
import { overrideKinds, type OverrideKind, type StorageLimitOverride } from './types'

export function isOverrideKind(value: string): value is OverrideKind {
  return overrideKinds.some((kind) => kind === value)
}

export interface OverrideDraft { kind: OverrideKind; gb: string }

/** `error` is a user-facing message; `value` carries exact decimal bytes for `limited`. */
export function resolveOverride(draft: OverrideDraft): { value: StorageLimitOverride } | { error: string } {
  if (draft.kind !== 'limited') return { value: { kind: draft.kind } }
  const parsed = parseGb(draft.gb)
  return 'error' in parsed ? { error: gbErrorMessage(parsed.error) } : { value: { kind: 'limited', bytes: parsed.bytes } }
}
