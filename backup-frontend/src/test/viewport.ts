/** Pretends the browser is at least `lg` wide (true) or narrower (false). `setup.ts` removes it after every test. */
export function setViewport(large: boolean) {
  window.matchMedia = ((query: string) => ({
    matches: large, media: query, onchange: null,
    addEventListener: () => undefined, removeEventListener: () => undefined,
    addListener: () => undefined, removeListener: () => undefined, dispatchEvent: () => false,
  })) as typeof window.matchMedia
}
