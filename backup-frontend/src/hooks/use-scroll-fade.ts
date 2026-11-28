import { useEffect, useRef, useState, type CSSProperties } from 'react'

const fade = '1.5rem'

/** A row that scrolls sideways: fades the edge that has more content beyond it, and brings the current item (`[aria-current=page]` or `[aria-selected=true]`) into view once. */
export function useScrollFade<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  const [edges, setEdges] = useState({ start: false, end: false })
  useEffect(() => {
    const element = ref.current
    if (!element) return
    const update = () => setEdges(previous => {
      const start = element.scrollLeft > 1, end = element.scrollLeft + element.clientWidth < element.scrollWidth - 1
      return previous.start === start && previous.end === end ? previous : { start, end }
    })
    update()
    element.addEventListener('scroll', update, { passive: true })
    const observer = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(update)
    observer?.observe(element)
    return () => { element.removeEventListener('scroll', update); observer?.disconnect() }
  }, [])
  useEffect(() => {
    ref.current?.querySelector<HTMLElement>('[aria-current=page],[aria-selected=true]')?.scrollIntoView?.({ inline: 'center', block: 'nearest' })
  }, [])
  const mask = `linear-gradient(to right, ${edges.start ? `transparent, #000 ${fade}` : '#000, #000'}, ${edges.end ? `#000 calc(100% - ${fade}), transparent` : '#000, #000'})`
  const style: CSSProperties | undefined = edges.start || edges.end ? { maskImage: mask, WebkitMaskImage: mask } : undefined
  return { ref, style }
}
