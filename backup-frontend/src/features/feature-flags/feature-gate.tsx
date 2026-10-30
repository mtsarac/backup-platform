import type { ReactNode } from 'react'
import { NotFound } from '@/components/layout/not-found'
import { isFeatureEnabled, type FeatureKey } from './feature-flags'

/** Route element wrapper: a disabled feature's URL is a plain not-found page. */
export function FeatureGate({ feature, children }: { feature: FeatureKey; children: ReactNode }) {
  return isFeatureEnabled(feature) ? children : <NotFound />
}
