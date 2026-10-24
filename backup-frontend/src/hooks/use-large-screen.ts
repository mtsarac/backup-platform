import { useSyncExternalStore } from 'react'

const LARGE_SCREEN = '(min-width: 1024px)'

function subscribe(onChange: () => void) {
  const query = window.matchMedia(LARGE_SCREEN)
  query.addEventListener('change', onChange)
  return () => query.removeEventListener('change', onChange)
}

/** Whether the viewport is at least the Tailwind `lg` width. A browser that cannot report it gets the large layout. */
export function useLargeScreen() {
  return useSyncExternalStore(
    typeof window.matchMedia === 'function' ? subscribe : () => () => undefined,
    () => typeof window.matchMedia !== 'function' || window.matchMedia(LARGE_SCREEN).matches,
  )
}
