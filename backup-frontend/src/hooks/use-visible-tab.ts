import { useEffect, useState } from 'react'

/** Whether the tab is in the foreground; polling hooks use it so a hidden tab makes no requests. */
export function useVisibleTab() {
  const [visible, setVisible] = useState(() => document.visibilityState === 'visible')
  useEffect(() => {
    const update = () => setVisible(document.visibilityState === 'visible')
    document.addEventListener('visibilitychange', update)
    return () => document.removeEventListener('visibilitychange', update)
  }, [])
  return visible
}
