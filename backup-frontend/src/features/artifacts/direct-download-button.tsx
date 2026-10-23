import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { tx, useLanguage } from '@/lib/i18n'
import type { PreparedDownload } from './api'
import { chooseDownloadFile, receiveDirectDownload } from './direct-download'

export function DirectDownloadButton({ session }: { session: PreparedDownload }) {
  useLanguage()
  const lifetime = useRef<AbortController | null>(null)
  const [state, setState] = useState<'idle' | 'saving' | 'saved' | 'failed'>('idle')
  const [error, setError] = useState('')
  useEffect(() => () => lifetime.current?.abort(), [])
  return <div className="space-y-2">
    <Notice>{tx('Dosya yetkili Agent ile tarayıcı arasında şifreli doğrudan kanaldan diske yazılır. Backend dosya baytı taşımaz; doğrudan bağlantı kurulamazsa indirme durur.')}</Notice>
    <Button disabled={state !== 'idle'} onClick={async () => {
      if (lifetime.current) return
      const controller = new AbortController()
      lifetime.current = controller
      try {
        const selected = chooseDownloadFile(session.filename)
        setState('saving')
        await receiveDirectDownload(session, await selected, controller.signal)
        if (!controller.signal.aborted) setState('saved')
      } catch (cause) {
        if (controller.signal.aborted) return
        if (cause instanceof DOMException && cause.name === 'AbortError') { lifetime.current = null; setState('idle'); return }
        setError(cause instanceof Error && cause.message === 'download_integrity_mismatch'
          ? tx('Dosya bütünlüğü doğrulanamadı; hedef dosya yayımlanmadı.')
          : tx('DirectPathUnavailable: Doğrudan Agent bağlantısı veya güvenli diske yazma desteği kullanılamıyor. Backend aktarımına geçilmez.'))
        setState('failed')
      }
    }}>{state === 'saving' ? tx('Doğrudan aktarım sürüyor…') : state === 'saved' ? tx('Doğrulandı ve diske kaydedildi.') : tx('Dosyayı kaydet')}</Button>
    {error && <Notice tone="error">{error}</Notice>}
  </div>
}
