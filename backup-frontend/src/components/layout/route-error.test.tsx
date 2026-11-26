import { describe, expect, it, vi } from 'vitest'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { render, screen } from '@/test/render'
import { RouteError } from './route-error'

function Broken(): never { throw new Error('render failed') }

describe('RouteError', () => {
  it('replaces a crashed page with an alert and a reload button', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const router = createMemoryRouter([{ path: '*', element: <Broken />, errorElement: <RouteError /> }])
    render(<RouterProvider router={router} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Sayfa yüklenemedi.')
    expect(screen.getByRole('button', { name: 'Sayfayı yeniden yükle' })).toBeEnabled()
  })
})
