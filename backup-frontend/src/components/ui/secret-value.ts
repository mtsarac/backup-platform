import { useState } from 'react'

/** State for one write-only secret: `take()` hands the value to the submit once and empties the field in the same step. */
export function useSecretValue() {
  const [value, set] = useState('')
  return { value, set, take() { const taken = value; set(''); return taken } }
}
