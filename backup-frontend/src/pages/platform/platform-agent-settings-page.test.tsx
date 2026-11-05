import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/platform/agent-settings/api'
import type { AgentSettings } from '@/features/platform/agent-settings/api'
import { renderPlatform, tenantOwner } from '@/features/platform/test-utils'
import { PlatformAgentSettingsPage } from './platform-agent-settings-page'
import { ApiError } from '@/lib/api/client'

vi.mock('@/features/platform/agent-settings/api')

const sampleSettings: AgentSettings = {
  serverUrl: 'https://backup.example.com:8443',
  enrollmentTokenLifetimeMinutes: 1440,
  limits: { minMinutes: 5, maxMinutes: 10080 },
  updatedAt: '2026-10-27T10:00:00Z',
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getAgentSettings).mockResolvedValue(sampleSettings)
})

describe('PlatformAgentSettingsPage', () => {
  it('loads and displays existing agent settings', async () => {
    renderPlatform(<PlatformAgentSettingsPage />)
    expect(await screen.findByDisplayValue('https://backup.example.com:8443')).toBeVisible()
    expect(screen.getByDisplayValue('1440')).toBeVisible()
  })

  it('saves updated settings', async () => {
    vi.mocked(api.updateAgentSettings).mockResolvedValue({
      ...sampleSettings,
      serverUrl: 'https://new-backup.example.com',
      enrollmentTokenLifetimeMinutes: 2880,
    })
    const user = userEvent.setup()
    renderPlatform(<PlatformAgentSettingsPage />)

    const serverInput = await screen.findByLabelText('Sunucu HTTPS adresi')
    const lifetimeInput = screen.getByLabelText('Kayıt anahtarının geçerlilik süresi (dakika)')
    await user.clear(serverInput)
    await user.type(serverInput, 'https://new-backup.example.com')
    await user.clear(lifetimeInput)
    await user.type(lifetimeInput, '2880')

    await user.click(screen.getByRole('button', { name: 'Kaydet' }))

    await waitFor(() => {
      expect(api.updateAgentSettings).toHaveBeenCalledWith({
        serverUrl: 'https://new-backup.example.com',
        enrollmentTokenLifetimeMinutes: 2880,
      })
    })
    expect(await screen.findByText('Yedekleme bilgisayarı ayarları kaydedildi.')).toBeVisible()
  })

  it('shows server field error for invalid_server_url', async () => {
    vi.mocked(api.updateAgentSettings).mockRejectedValue(new ApiError(400, { error: 'invalid_server_url' }))
    const user = userEvent.setup()
    renderPlatform(<PlatformAgentSettingsPage />)

    await screen.findByDisplayValue('https://backup.example.com:8443')
    await user.click(screen.getByRole('button', { name: 'Kaydet' }))

    expect(await screen.findByText('Geçerli bir HTTPS adresi girin: yalnızca https://, sunucu adı ve isteğe bağlı port.')).toBeVisible()
  })

  it('shows server field error for invalid_lifetime', async () => {
    vi.mocked(api.updateAgentSettings).mockRejectedValue(new ApiError(400, { error: 'invalid_lifetime' }))
    const user = userEvent.setup()
    renderPlatform(<PlatformAgentSettingsPage />)

    await screen.findByDisplayValue('1440')
    await user.click(screen.getByRole('button', { name: 'Kaydet' }))

    expect(await screen.findByText(/5 ile 10080 dakika arasında bir tam sayı olmalı/)).toBeVisible()
  })

  it('forbids access for non-platform users', async () => {
    vi.mocked(api.getAgentSettings).mockRejectedValue(new ApiError(403))
    renderPlatform(<PlatformAgentSettingsPage />, { user: tenantOwner })
    expect(await screen.findByRole('alert')).toHaveTextContent(/Platform Yöneticisi/)
  })
})
