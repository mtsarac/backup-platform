import { tx } from '@/lib/i18n'
import { useContext } from 'react'
import { AuthContext } from './auth-context.ts'

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error(tx('useAuth must be used within AuthProvider'))
  return ctx
}
