import { Button } from '@/components/ui/button'
import { tx, useLanguage } from '@/lib/i18n'

/** Last resort for a render error anywhere below the router: a message and a reload instead of a blank page. */
export function RouteError() {
  useLanguage()
  return <main className="flex min-h-svh items-center justify-center bg-surface-2">
    <div role="alert" className="mx-auto max-w-lg space-y-4 p-6 text-center">
      <h1 className="text-lg font-semibold">{tx("Sayfa yüklenemedi.")}</h1>
      <p className="text-text-muted">{tx("Beklenmeyen bir hata oluştu. Sayfayı yeniden yükleyin; sürerse destek ekibine bildirin.")}</p>
      <Button onClick={() => window.location.reload()}>{tx("Sayfayı yeniden yükle")}</Button>
    </div>
  </main>
}
