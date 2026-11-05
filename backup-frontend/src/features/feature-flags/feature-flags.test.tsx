import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Bell } from 'lucide-react'
import { afterEach, describe, expect, it } from 'vitest'
import { SidebarFrame } from '@/components/layout/nav'
import { setFeatureFlags } from './feature-flags'
import { FeatureGate } from './feature-gate'

afterEach(() => setFeatureFlags({}))

describe('feature flags', () => {
  it('hides a flagged route and nav entry until the feature is on', () => {
    const ui = <MemoryRouter>
      <SidebarFrame dialogLabel="d" label="nav" brand={{ icon: Bell, title: 't', caption: 'c' }} groups={[{ items: [{ label: 'Gizli', to: '/x', icon: Bell, feature: 'sms' }] }]} />
      <FeatureGate feature="sms"><p>flagged page</p></FeatureGate>
    </MemoryRouter>
    const { unmount } = render(ui)
    expect(screen.queryByRole('link', { name: 'Gizli' })).not.toBeInTheDocument()
    expect(screen.queryByText('flagged page')).not.toBeInTheDocument()
    unmount()
    setFeatureFlags({ sms: true })
    render(ui)
    expect(screen.getAllByRole('link', { name: 'Gizli' }).length).toBeGreaterThan(0)
    expect(screen.getByText('flagged page')).toBeInTheDocument()
  })
})
