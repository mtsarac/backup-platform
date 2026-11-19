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
  vi.mocked(api.getAgentDistribution).mockResolvedValue({ serverUrl: 'https://backup.corp.internal:8443', packages: { 'win-x64': true, 'linux-x64': true, 'win-x64-setup': true } })
  vi.mocked(api.createEnrollmentToken).mockResolvedValue({ tokenId: 'token-a', token: "secret'$(untrusted)", expiresAt: '2027-10-17T12:00:00Z' })
})
function mount() {
  return render(<QueryClientProvider client={new QueryClient()}><AddAgentDialog tenantId="tenant-test" onClose={vi.fn()} /></QueryClientProvider>)
}

describe('One-line installation consumers', () => {
  it('uses the configured public HTTPS URL and bypasses the PowerShell execution policy', async () => {
    mount()
    fireEvent.change(await screen.findByRole('textbox', { name: 'Bilgisayarın adı' }), { target: { value: 'Office' } })
    fireEvent.click(screen.getByRole('button', { name: 'İleri' }))
    fireEvent.click(screen.getByRole('button', { name: 'Kayıt anahtarı oluştur' }))
    expect(await screen.findByDisplayValue("secret'$(untrusted)")).toBeInTheDocument()
    const command = (screen.getByRole('textbox', { name: 'Tek kullanımlık kurulum komutu' }) as HTMLTextAreaElement).value
    expect(command).toContain('https://backup.corp.internal:8443/api/agent-dist/install.ps1')
    expect(command).toContain('powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\\install.ps1"')
    expect(command).toContain("-Token 'secret''$(untrusted)' -AgentName 'Office' -NonInteractive -Language tr")
  })

  it('localizes English commands and offers other install methods collapsed', async () => {
    localStorage.setItem('bp_language', 'en')
    mount()
    fireEvent.change(await screen.findByRole('textbox', { name: 'Computer name' }), { target: { value: "O'Brien" } })
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create enrollment key' }))
    await screen.findByDisplayValue("secret'$(untrusted)")
    const command = screen.getByRole('textbox', { name: 'One-time installation command' }) as HTMLTextAreaElement
    expect(command.value).toContain("-Token 'secret''$(untrusted)' -AgentName 'O''Brien' -NonInteractive -Language en")
    expect(screen.queryByRole('tab')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy installation command' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Back' })).toBeEnabled()
    expect(screen.getByText('Other installation methods').closest('details')).not.toHaveAttribute('open')
    expect(screen.getByRole('link', { name: 'Download installation script' })).toHaveAttribute('href', 'https://backup.corp.internal:8443/api/agent-dist/install.ps1')
    expect(screen.getByRole('link', { name: 'Download Windows zip package' })).toHaveAttribute('href', 'https://backup.corp.internal:8443/api/agent-dist/packages/win-x64')
    expect(document.documentElement.lang).toBe('en')
  })

  async function openOtherMethods(platform: 'Windows' | 'Linux') {
    mount()
    fireEvent.change(await screen.findByRole('textbox', { name: 'Bilgisayarın adı' }), { target: { value: 'Office' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'İşletim sistemi' }), { target: { value: platform } })
    fireEvent.click(screen.getByRole('button', { name: 'İleri' }))
    fireEvent.click(screen.getByRole('button', { name: 'Kayıt anahtarı oluştur' }))
    await screen.findByDisplayValue("secret'$(untrusted)")
  }

  it('offers the published Windows setup wizard with the key and says why SmartScreen warns', async () => {
    await openOtherMethods('Windows')
    expect(screen.getByRole('link', { name: 'Kurulum sihirbazını indir (.exe)' })).toHaveAttribute('href', 'https://backup.corp.internal:8443/api/agent-dist/packages/win-x64-setup')
    expect(screen.getByRole('textbox', { name: 'Tek kullanımlık kayıt anahtarı' })).toHaveValue("secret'$(untrusted)")
    expect(screen.getByText(/dijital olarak imzalı olmadığından Windows SmartScreen/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Kurulum scriptini indir' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Windows zip paketini indir' })).toBeInTheDocument()
  })

  it('explains an unpublished setup wizard and keeps the script and zip', async () => {
    vi.mocked(api.getAgentDistribution).mockResolvedValue({ serverUrl: 'https://backup.corp.internal:8443', packages: { 'win-x64': true, 'linux-x64': true, 'win-x64-setup': false } })
    await openOtherMethods('Windows')
    expect(screen.queryByRole('link', { name: 'Kurulum sihirbazını indir (.exe)' })).not.toBeInTheDocument()
    expect(screen.getByText('Windows kurulum sihirbazı (.exe) yayımlanmamış. Şimdilik yukarıdaki PowerShell komutunu ya da aşağıdaki script ve zip paketini kullanın.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Kurulum scriptini indir' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Windows zip paketini indir' })).toBeInTheDocument()
  })

  it('does not mention the Windows wizard for Linux', async () => {
    await openOtherMethods('Linux')
    expect(screen.queryByRole('link', { name: 'Kurulum sihirbazını indir (.exe)' })).not.toBeInTheDocument()
    expect(screen.queryByText(/SmartScreen/)).not.toBeInTheDocument()
  })
})
