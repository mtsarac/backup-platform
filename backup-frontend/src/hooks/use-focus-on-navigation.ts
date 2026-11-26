import { useEffect, useRef, type RefObject } from 'react'
import { useLocation } from 'react-router-dom'

let lastPath: string | undefined

/** Forgets the page the user was on, so the next mount counts as a first load (tests). */
export function resetFocusOnNavigation() { lastPath = undefined }

/**
 * Focuses `target` when the component mounts because the user navigated, so keyboard and screen reader users land on the new page instead of the link they activated.
 * The first page load keeps the normal focus order, and a component that stays mounted across routes (a layout heading above tabs) never pulls focus off the tab.
 */
export function useFocusOnNavigation(target: RefObject<HTMLElement | null>) {
  const { pathname } = useLocation()
  const mountedAt = useRef(pathname)
  useEffect(() => {
    if (lastPath !== undefined && lastPath !== mountedAt.current) target.current?.focus()
    lastPath = mountedAt.current
  }, [target])
}
