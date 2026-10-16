import { useState } from 'react'
import { Check, Copy } from 'lucide-react'
import { Button } from '@/components/ui/button'

/** Copies text to the clipboard; the label reports the outcome (never relies on color or icon alone). */
export function CopyButton({ value, label }: { value: string; label: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')
  async function copy() {
    try { await navigator.clipboard.writeText(value); setState('copied') } catch { setState('failed') }
    window.setTimeout(() => setState('idle'), 2500)
  }
  return <Button type="button" variant="outline" size="sm" aria-label={label} onClick={() => void copy()}>
    {state === 'copied' ? <Check aria-hidden="true" className="size-4" /> : <Copy aria-hidden="true" className="size-4" />}
    {state === 'copied' ? 'Kopyalandı' : state === 'failed' ? 'Kopyalanamadı' : 'Kopyala'}
  </Button>
}
