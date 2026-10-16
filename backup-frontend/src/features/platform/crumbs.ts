import type { Crumb } from '@/components/ui/page-header'
import { platformPaths } from './paths'

/** The subtle root crumb every PlatformAdmin screen starts with. */
export const platformCrumb: Crumb = { label: 'Platform', to: platformPaths.root }
