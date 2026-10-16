import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AddAgentDialog } from './add-agent-dialog'
import * as api from './api'

vi.mock('./api')
vi.mock('./use-enrollment-monitor', () => ({
  useEnrollmentMonitor: () => ({ phase: 'Waiting', agent: null, error: '', paused: false, retry: vi.fn() }),
}))

beforeEach(() => {
  vi.resetAllMocks()
  localStorage.removeItem('bp_language')
  vi.mocked(api.getAgentDistribution).mockResolvedValue({ serverUrl: 'https://backup.corp.internal:8443', packages: { 'win-x64': true, 'linux-x64': true } })
  vi.mocked(api.createEnrollmentToken).mockResolvedValue({ tokenId: 'token-a', token: "secret'$(untrusted)", expiresAt: '2027-10-17T12:00:00Z' })
})
function mount() {
  return render(<QueryClientProvider client={new QueryClient()}><AddAgentDialog tenantId="tenant-test" onClose={vi.fn()} /></QueryClientProvider>)
}

describe('One-line installation consumers', () => {
  it('uses the configured public HTTPS URL and never places the key in guided commands', async () => {
    mount()
    await screen.findByDisplayValue('https://backup.corp.internal:8443')
    fireEvent.click(screen.getByRole('button', { name: 'Kayıt anahtarı oluştur' }))
    expect(await screen.findByDisplayValue("secret'$(untrusted)")).toBeInTheDocument()
    const command = screen.getByRole('textbox', { name: 'Tek kullanımlık kurulum komutu' })
    expect((command as HTMLTextAreaElement).value).toContain('/api/agent-dist/install.ps1')
    expect((command as HTMLTextAreaElement).value).toContain('-Interactive -Language tr')
    expect((command as HTMLTextAreaElement).value).not.toContain('secret')
    fireEvent.change(screen.getByRole('combobox', { name: 'Platform' }), { target: { value: 'Linux' } })
    expect((command as HTMLTextAreaElement).value).toContain('/api/agent-dist/install.sh')
    expect((command as HTMLTextAreaElement).value).toContain('--interactive --language tr')
  })

  it('localizes English commands and disables copying for invalid origins and missing automatic names', async () => {
    localStorage.setItem('bp_language', 'en')
    mount()
    await screen.findByDisplayValue('https://backup.corp.internal:8443')
    fireEvent.click(screen.getByRole('button', { name: 'Create enrollment key' }))
    await screen.findByDisplayValue("secret'$(untrusted)")
    fireEvent.click(screen.getByRole('tab', { name: 'Automatic installation' }))
    expect(screen.getByRole('button', { name: 'Copy installation command' })).toBeDisabled()
    fireEvent.change(screen.getByRole('textbox', { name: 'Agent name' }), { target: { value: "O'Brien" } })
    const command = screen.getByRole('textbox', { name: 'One-time installation command' }) as HTMLTextAreaElement
    expect(command.value).toContain("-Token 'secret''$(untrusted)' -AgentName 'O''Brien' -NonInteractive -Language en")
    fireEvent.change(screen.getByRole('textbox', { name: 'Server HTTPS address' }), { target: { value: 'http://localhost:5173' } })
    expect(command.value).toBe('')
    expect(screen.getByRole('button', { name: 'Copy installation command' })).toBeDisabled()
    expect(document.documentElement.lang).toBe('en')
  })
})
