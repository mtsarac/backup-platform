import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { afterEach, describe, expect, it } from 'vitest'
import { setFeatureFlags } from '@/features/feature-flags/feature-flags'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { SourceStep } from './source-step'

afterEach(() => setFeatureFlags({}))

const form = { values: { name: '', agentId: '', selected: [], sourceMode: 'manual', enabled: true, configuration: { engine: 'file-system' } }, errors: {}, agents: { data: [] }, set: () => {}, setConfiguration: () => {}, job: undefined } as unknown as JobFormState

describe('file source hint', () => {
  it.each([['windowsVss'], ['linuxSnapshots']] as const)('mentions snapshot sources only while %s is on', (flag) => {
    const { unmount } = render(<SourceStep form={form} />)
    expect(screen.queryByText(/anlık görüntü kaynağı/)).not.toBeInTheDocument()
    unmount()
    setFeatureFlags({ [flag]: true })
    render(<SourceStep form={form} />)
    expect(screen.getByText(/anlık görüntü kaynağı/)).toBeInTheDocument()
  })
})
