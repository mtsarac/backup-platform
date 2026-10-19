import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { describe, expect, it } from 'vitest'
import { ArtifactEncryptionSection } from './artifact-encryption-section'
import { keyId } from './test-fixtures'

describe('artifact encryption facts', () => {
  it('states that a plain artifact is v1-plain instead of hiding the section', () => {
    render(<ArtifactEncryptionSection encryption={null} />)
    expect(screen.getByText('v1-plain · şifresiz')).toBeInTheDocument()
    expect(screen.queryByText('Anahtar sürümü')).not.toBeInTheDocument()
  })

  it('shows format, key version and recipients, and only a short key id', () => {
    render(<ArtifactEncryptionSection encryption={{ format: 'v2-bpe', keyId, keyVersion: 3, recipients: [
      { type: 'tenant-wrap', keyId, keyVersion: 3 }, { type: 'platform-recovery', keyId: 'b'.repeat(32), keyVersion: 1 },
    ] }} />)
    expect(screen.getByText('v2-bpe · şifreli (AES-256-GCM)')).toBeInTheDocument()
    expect(screen.getByText('Platform kurtarma alıcısı').nextSibling).toHaveTextContent('Var')
    expect(screen.getByText(/Tenant anahtarı \(backend\) · v3/)).toBeInTheDocument()
    expect(screen.getByText(/Platform kurtarma anahtarı · v1/)).toBeInTheDocument()
    expect(document.body.textContent).not.toContain(keyId)
    expect(screen.getAllByTitle(keyId).length).toBeGreaterThan(0)
  })

  it('reports a missing platform recovery recipient', () => {
    render(<ArtifactEncryptionSection encryption={{ format: 'v2-bpe', keyId, keyVersion: 1, recipients: [{ type: 'tenant-wrap', keyId, keyVersion: 1 }] }} />)
    expect(screen.getByText('Platform kurtarma alıcısı').nextSibling).toHaveTextContent('Yok')
  })
})
