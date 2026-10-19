import { fireEvent, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { BreakGlassRequestDialog } from './break-glass-request-dialog'
import { makeBreakGlass } from './test-fixtures'

vi.mock('./api')
beforeEach(() => vi.resetAllMocks())
const tenantId = '22222222-2222-2222-2222-222222222222'
const artifactId = '11111111-1111-1111-1111-111111111111'
const mount = (props: Partial<Parameters<typeof BreakGlassRequestDialog>[0]> = {}) => renderPlatform(<BreakGlassRequestDialog onClose={vi.fn()} {...props} />)

describe('break-glass request dialog', () => {
  it('validates ids, reason and TTL before any request', async () => {
    mount()
    fireEvent.change(await screen.findByLabelText('Tenant kimliği'), { target: { value: 'nope' } })
    fireEvent.change(screen.getByLabelText('Süre (dakika)'), { target: { value: '500' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    expect(await screen.findByText('Geçerli bir tenant kimliği (GUID) girin.')).toBeInTheDocument()
    expect(screen.getByText('Geçerli bir Backup kimliği (GUID) girin.')).toBeInTheDocument()
    expect(screen.getByText('Break-glass için gerekçe girin.')).toBeInTheDocument()
    expect(screen.getByText('Süre 1 ile 240 dakika arasında tam sayı olmalıdır.')).toBeInTheDocument()
    expect(api.createBreakGlassRequest).not.toHaveBeenCalled()
  })

  it('opens the request with the prefilled target and navigates to it', async () => {
    vi.mocked(api.createBreakGlassRequest).mockResolvedValue(makeBreakGlass({ id: 'new-request' }))
    vi.mocked(api.getBreakGlassRequest).mockResolvedValue(makeBreakGlass({ id: 'new-request' }))
    mount({ initialTenantId: tenantId, initialArtifactId: artifactId })
    fireEvent.change(await screen.findByLabelText('Gerekçe'), { target: { value: '  incident 1  ' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    await waitFor(() => expect(api.createBreakGlassRequest).toHaveBeenCalledWith({ tenantId, artifactId, reason: 'incident 1', ttlMinutes: 60 }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/break-glass/new-request'))
  })

  it('shows why the server refused an artifact that is not encrypted', async () => {
    vi.mocked(api.createBreakGlassRequest).mockRejectedValue(new ApiError(409, { error: 'artifact_not_encrypted' }))
    mount({ initialTenantId: tenantId, initialArtifactId: artifactId })
    fireEvent.change(await screen.findByLabelText('Gerekçe'), { target: { value: 'because' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    expect(await screen.findByText('Bu Backup şifreli değil; şifre çözme veya break-glass uygulanamaz.')).toBeInTheDocument()
  })
})
