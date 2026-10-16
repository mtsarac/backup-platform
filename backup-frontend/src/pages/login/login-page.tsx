import { useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { DatabaseBackup, LockKeyhole, ShieldCheck, Workflow } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Notice } from '@/components/ui/notice'
import { useAuth } from '@/features/auth/use-auth'
import { inputClass } from '@/features/platform/styles'
import { ROUTES } from '@/lib/routes'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

const promises = [
  { icon: ShieldCheck, text: 'Her yedek doğrulanır ve checksum ile kayıt altına alınır.' },
  { icon: Workflow, text: 'Zamanlama, yeniden deneme ve saklama süreleri tek yerden yönetilir.' },
  { icon: LockKeyhole, text: 'Her müşterinin verisi kendi alanında, ayrı yetkilerle tutulur.' },
]

export function LoginPage() {
  const { login, isAuthenticated, sessionExpired } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)

  const from = (location.state as { from?: { pathname: string } })?.from?.pathname ?? ROUTES.DASHBOARD

  if (isAuthenticated) {
    return <Navigate to={from} replace />
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    setLoading(true)
    try {
      await login(email, password)
      setPassword('')
      navigate(from, { replace: true })
    } catch (error) {
      setError(error instanceof ApiError && error.status === 401 ? 'Kullanıcı adı veya şifre hatalı.' : errorMessage(error))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="grid min-h-svh bg-canvas lg:grid-cols-[minmax(0,5fr)_minmax(0,6fr)]">
      <aside className="hidden flex-col justify-between bg-sidebar p-12 text-sidebar-foreground lg:flex">
        <div className="flex items-center gap-3">
          <span className="flex size-10 items-center justify-center rounded-lg bg-primary text-primary-foreground"><DatabaseBackup aria-hidden="true" className="size-6" /></span>
          <span className="text-lg font-semibold">Backup Platform</span>
        </div>
        <div className="max-w-md space-y-8">
          <h2 className="text-3xl font-semibold leading-tight text-white">Veritabanı yedekleriniz, tek bakışta güvende.</h2>
          <ul className="space-y-4">{promises.map(({ icon: Icon, text }) => <li key={text} className="flex gap-3"><Icon aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-primary" /><span className="text-[0.9375rem] text-sidebar-foreground">{text}</span></li>)}</ul>
        </div>
        <p className="text-sm text-sidebar-muted">Yönetim konsolu</p>
      </aside>
      <main className="flex items-center justify-center px-4 py-10">
        <div className="w-full max-w-sm space-y-6">
          <div className="flex items-center gap-3 lg:hidden">
            <span className="flex size-9 items-center justify-center rounded-lg bg-primary text-primary-foreground"><DatabaseBackup aria-hidden="true" className="size-5" /></span>
            <span className="font-semibold text-text-heading">Backup Platform</span>
          </div>
          <div className="space-y-1"><h1 className="text-2xl font-semibold text-text-heading">Giriş yapın</h1><p className="text-[0.9375rem] text-text-muted">Hesabınızla yönetim konsoluna devam edin.</p></div>
          <form onSubmit={handleSubmit} className="space-y-4 rounded-xl border border-border bg-surface p-6 shadow-card">
            <Field label="Kullanıcı adı">{(control) => <input {...control} type="text" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} className={`${inputClass} mt-0`} required />}</Field>
            <Field label="Şifre">{(control) => <input {...control} type="password" value={password} autoComplete="current-password" onChange={(e) => setPassword(e.target.value)} className={`${inputClass} mt-0`} required />}</Field>
            {sessionExpired && <Notice tone="warning">Oturumunuz sona erdi. Yeniden giriş yapın.</Notice>}
            {error && <Notice tone="error">{error}</Notice>}
            <Button type="submit" size="lg" className="w-full" disabled={loading}>{loading ? 'Giriş yapılıyor...' : 'Giriş Yap'}</Button>
          </form>
          <p className="text-sm text-text-muted">Oturum güvenli cookie ile yönetilir. Tenant erişimleriniz girişten sonra gösterilir.</p>
        </div>
      </main>
    </div>
  )
}
