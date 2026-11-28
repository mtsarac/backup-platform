import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { FileRestoreTargetStep } from './file-restore-target-step'

describe('restore wizard reasons', () => {
  it('shows the "Neden soruluyor?" label once for a step with two questions', () => {
    render(<FileRestoreTargetStep instancePrefix="t" originalAgent={undefined} original={false} overwrite={false} precheckError={null} precheckRetryable={false} precheckStarting={false} restoreStarting={false} canCheck checkPanel={null} onLocationChange={() => undefined} onOverwriteChange={() => undefined} onBack={() => undefined} onContinue={() => undefined} />)
    expect(screen.getAllByRole('heading', { level: 2 })).toHaveLength(2)
    expect(screen.getAllByText('Neden soruluyor?')).toHaveLength(1)
    expect(screen.getByText(/Hedefte aynı adlı bir dosya varsa/)).toBeInTheDocument()
  })
})
