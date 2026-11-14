import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Navigate } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { ThemeProvider } from '@/components/theme-provider'
import { AccountShell } from '@/components/layout/account-shell'
import { renderPlatform } from '@/features/platform/test-utils'
import { AccountLayout } from './account-layout'
import { AccountPage } from './account-page'

const tree = <>
  <Route path="/account" element={<ThemeProvider><AccountShell /></ThemeProvider>}>
    <Route element={<AccountLayout />}>
      <Route index element={<AccountPage />} />
      {['sessions', 'security', 'passkeys'].map((page) => <Route key={page} path={page} element={<p>{page} page</p>} />)}
    </Route>
  </Route>
  {['sessions', 'security', 'passkeys'].map((page) => <Route key={page} path={`/${page}`} element={<Navigate to={`/account/${page}`} replace />} />)}
</>

describe('Account settings', () => {
  it('shows profile facts from the signed-in user and signs out', async () => {
    const { auth } = renderPlatform(null, { path: '/account', routes: tree })
    expect(await screen.findByRole('heading', { name: 'Hesap', level: 1 })).toBeInTheDocument()
    expect(screen.getAllByText('Platform Operatörü').length).toBeGreaterThan(0)
    await userEvent.click(screen.getByRole('button', { name: 'Çıkış' }))
    expect(auth.logout).toHaveBeenCalled()
  })

  it('marks the open sub-page current in the sub-navigation and the settings entry in the sidebar', async () => {
    renderPlatform(null, { path: '/account/security', routes: tree })
    const tabs = within(await screen.findByRole('navigation', { name: 'Hesap ayarları' }))
    expect(tabs.getAllByRole('link').map((link) => [link.textContent, link.getAttribute('href')])).toEqual([
      ['Hesap', '/account'], ['Oturumlarım', '/account/sessions'], ['Güvenlik', '/account/security'], ['Geçiş anahtarları', '/account/passkeys'],
    ])
    expect(tabs.getByRole('link', { current: 'page' })).toHaveTextContent('Güvenlik')
    expect(within(screen.getAllByRole('navigation', { name: 'Ana gezinme' })[0].closest('aside')!).getByRole('link', { current: 'page' })).toHaveTextContent('Ayarlar')
  })

  it.each(['sessions', 'security', 'passkeys'])('redirects the legacy /%s URL to its /account page', async (page) => {
    renderPlatform(null, { path: `/${page}`, routes: tree })
    expect(await screen.findByText(`${page} page`)).toBeInTheDocument()
  })
})
