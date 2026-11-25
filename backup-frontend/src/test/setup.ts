import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterEach, beforeEach } from 'vitest'
import { syncLanguagePreference } from '@/lib/i18n/runtime'

// The 1s findBy/waitFor default is shorter than a refetch under CPU contention; passing assertions still return immediately.
configure({ asyncUtilTimeout: 5_000 })

beforeEach(() => {
  localStorage.removeItem('bp_language')
  syncLanguagePreference()
})
afterEach(() => {
  cleanup()
  sessionStorage.clear()
  Reflect.deleteProperty(window, 'matchMedia')
})
