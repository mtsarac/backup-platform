import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach, beforeEach } from 'vitest'
import { syncLanguagePreference } from '@/lib/i18n/runtime'

beforeEach(() => {
  localStorage.removeItem('bp_language')
  syncLanguagePreference()
})
afterEach(() => {
  cleanup()
  Reflect.deleteProperty(window, 'matchMedia')
})
