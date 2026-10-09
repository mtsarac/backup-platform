/** Parsing of the free-text values a quota policy scope selector takes. The backend validates again; this only spares a round trip. */
export const QUOTA_SCOPE_PATTERN = /^[a-z0-9-]{1,64}$/
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export type ScopeValuesResult = { ok: true; values: string[] } | { ok: false; error: string }

function tokens(text: string) {
  return [...new Set(text.split(/[\s,;]+/).filter((token) => token.length > 0))]
}

/** Quota scope labels: lowercase letters, digits and `-`, 1-64 characters each. Labels are not case-folded: `Managed` is rejected, not silently changed. */
export function parseQuotaScopeLabels(text: string): ScopeValuesResult {
  const labels = tokens(text)
  if (labels.length === 0) return { ok: false, error: 'En az bir kota kapsamı etiketi girmelisiniz (örn. managed, tenant).' }
  const invalid = labels.find((label) => !QUOTA_SCOPE_PATTERN.test(label))
  if (invalid) return { ok: false, error: `“${invalid.slice(0, 40)}” geçerli bir kota kapsamı etiketi değil: yalnızca küçük harf, rakam ve “-”, en fazla 64 karakter.` }
  return { ok: true, values: labels }
}

/** Connection GUIDs, normalised to the canonical lowercase `D` format the backend compares against. */
export function parseStorageConnectionIds(text: string): ScopeValuesResult {
  const ids = tokens(text)
  if (ids.length === 0) return { ok: false, error: 'En az bir depolama bağlantısı kimliği (GUID) girmelisiniz.' }
  const invalid = ids.find((id) => !GUID_PATTERN.test(id))
  if (invalid) return { ok: false, error: `“${invalid.slice(0, 40)}” geçerli bir GUID değil (örn. 3f2b8c1e-0d4a-4b6e-9a7c-5e1d2f3a4b5c).` }
  return { ok: true, values: [...new Set(ids.map((id) => id.toLowerCase()))] }
}
