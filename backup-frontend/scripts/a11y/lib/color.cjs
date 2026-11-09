// Colour maths shared by contrast-audit.mjs and audit.mjs. One file, two loaders:
//   Node/Bun: require('./color.cjs')            (validation of results, unit tests)
//   Chromium: page.addScriptTag({ path })       (defines window.__a11yColor, measures computed styles)
//
// Every function throws on a value outside the valid range instead of returning it, so an impossible number
// (channel above 255, ratio above 21:1) can never reach a results file.
// Root cause of the invalid values the first version produced: canvas getImageData() returns NON-premultiplied
// channels, and the old code divided them by alpha again, so any translucent colour overflowed
// (e.g. 255 / 0.95 = 268, which also pushed black-on-"white" to 23.4:1).
(function (root) {
  const CHANNEL_MAX = 255
  const RATIO_MAX = 21
  const EPS = 1e-6

  function assertColor(c, what) {
    if (!Array.isArray(c) || c.length !== 4 || c.some((v) => typeof v !== 'number' || Number.isNaN(v))) throw new RangeError(`${what}: not an [r,g,b,a] colour: ${JSON.stringify(c)}`)
    for (let i = 0; i < 3; i++) if (c[i] < -EPS || c[i] > CHANNEL_MAX + EPS) throw new RangeError(`${what}: channel ${i} out of 0..255: ${JSON.stringify(c)}`)
    if (c[3] < -EPS || c[3] > 1 + EPS) throw new RangeError(`${what}: alpha out of 0..1: ${JSON.stringify(c)}`)
    return c
  }

  const num = (s) => (s.endsWith('%') ? parseFloat(s) / 100 : parseFloat(s))

  /** Pure parser for rgb()/rgba()/#hex/transparent; returns null for anything else. */
  function parseSimple(css) {
    const s = String(css).trim().toLowerCase()
    if (s === 'transparent') return [0, 0, 0, 0]
    let m = /^#([0-9a-f]{3,8})$/.exec(s)
    if (m) {
      let h = m[1]
      if (h.length === 3 || h.length === 4) h = [...h].map((d) => d + d).join('')
      if (h.length !== 6 && h.length !== 8) return null
      return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)).concat(h.length === 8 ? parseInt(h.slice(6, 8), 16) / 255 : 1)
    }
    m = /^rgba?\(\s*([\d.]+%?)[\s,]+([\d.]+%?)[\s,]+([\d.]+%?)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$/.exec(s)
    if (m) {
      const ch = [m[1], m[2], m[3]].map((v) => (v.endsWith('%') ? (parseFloat(v) / 100) * CHANNEL_MAX : parseFloat(v)))
      return ch.concat(m[4] === undefined ? 1 : num(m[4]))
    }
    return null
  }

  let canvas
  /** Any CSS colour -> [r,g,b,a] (r,g,b 0..255, a 0..1). Non-rgb syntaxes (color-mix gives oklab, oklch, system colours) are rasterised by the browser. */
  function parse(css) {
    let c = parseSimple(css)
    if (!c) {
      if (typeof document === 'undefined') throw new Error(`cannot parse colour outside a browser: ${css}`)
      canvas ??= (() => { const el = document.createElement('canvas'); el.width = el.height = 1; return el.getContext('2d', { willReadFrequently: true }) })()
      canvas.clearRect(0, 0, 1, 1)
      canvas.fillStyle = '#010203'
      canvas.fillStyle = css
      canvas.fillRect(0, 0, 1, 1)
      // getImageData is NON-premultiplied (8 bit): use the channels as they are, never divide by alpha again.
      const d = canvas.getImageData(0, 0, 1, 1).data
      if (d[3] === 255 && d[0] === 1 && d[1] === 2 && d[2] === 3 && !/^#010203$/i.test(String(css).trim())) throw new Error(`unparseable colour: ${css}`)
      c = [d[0], d[1], d[2], d[3] / 255]
    }
    return assertColor(c, `parse(${css})`)
  }

  /** Source-over: `top` painted on `bottom` (both [r,g,b,a], not premultiplied). */
  function over(top, bottom) {
    assertColor(top, 'over(top)'); assertColor(bottom, 'over(bottom)')
    const a = top[3] + bottom[3] * (1 - top[3])
    if (a === 0) return [0, 0, 0, 0]
    const out = [0, 1, 2].map((i) => (top[i] * top[3] + bottom[i] * bottom[3] * (1 - top[3])) / a)
    return assertColor(out.concat(a), 'over(result)')
  }

  const withOpacity = (c, opacity) => [c[0], c[1], c[2], c[3] * opacity]

  function luminance(c) {
    assertColor(c, 'luminance')
    const f = (v) => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 }
    return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2])
  }

  /** WCAG contrast ratio of two OPAQUE colours, truncated (never rounded up, like axe-core) to 2 decimals; throws outside 1..21. */
  function ratio(a, b) {
    if (a[3] < 1 - EPS || b[3] < 1 - EPS) throw new RangeError(`ratio needs opaque colours, got alpha ${a[3]} / ${b[3]}`)
    const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p)
    const r = (x + 0.05) / (y + 0.05)
    if (r < 1 - EPS || r > RATIO_MAX + EPS) throw new RangeError(`contrast ratio out of 1..21: ${r}`)
    return Math.min(RATIO_MAX, Math.max(1, Math.floor(r * 100 + 1e-6) / 100))
  }

  const rgbString = (c) => { assertColor(c, 'rgbString'); return `rgb(${c.slice(0, 3).map((v) => Math.round(Math.min(CHANNEL_MAX, Math.max(0, v)))).join(', ')})` }

  /** Composited background of an element: walks up to the first opaque layer, then paints it over the root and white. */
  function backgroundOf(el) {
    const layers = []
    let hasImage = false
    for (let n = el; n; n = n.parentElement) {
      const cs = getComputedStyle(n)
      if (cs.backgroundImage !== 'none') hasImage = true
      const c = parse(cs.backgroundColor)
      layers.push(c)
      if (c[3] >= 1) break
    }
    let acc = over(parse(getComputedStyle(document.documentElement).backgroundColor), [255, 255, 255, 1])
    for (const l of layers.reverse()) acc = over(l, acc)
    return { color: acc, hasImage }
  }

  function inheritedOpacity(el) {
    let o = 1
    for (let n = el; n; n = n.parentElement) o *= Number(getComputedStyle(n).opacity)
    return o
  }

  /** Walks any JSON value; reports rgb()/rgba() channels outside 0..255 and any ratio-like number outside 1..21. */
  function validateDeep(value, path = '$') {
    const problems = []
    if (typeof value === 'string') {
      for (const m of value.matchAll(/rgba?\(([^)]*)\)/gi)) {
        const c = parseSimple(m[0])
        if (!c) { problems.push(`${path}: unparseable ${m[0]}`); continue }
        if (c.slice(0, 3).some((v) => v < 0 || v > CHANNEL_MAX) || c[3] < 0 || c[3] > 1) problems.push(`${path}: colour out of range ${m[0]}`)
      }
    } else if (typeof value === 'number') {
      if (/ratio$/i.test(path.split('.').pop() ?? '') && (value < 1 || value > RATIO_MAX)) problems.push(`${path}: ratio ${value} outside 1..21`)
    } else if (Array.isArray(value)) {
      value.forEach((v, i) => problems.push(...validateDeep(v, `${path}[${i}]`)))
    } else if (value && typeof value === 'object') {
      for (const [k, v] of Object.entries(value)) problems.push(...validateDeep(v, `${path}.${k}`))
    }
    return problems
  }

  const api = { CHANNEL_MAX, RATIO_MAX, assertColor, parseSimple, parse, over, withOpacity, luminance, ratio, rgbString, backgroundOf, inheritedOpacity, validateDeep }
  if (typeof module !== 'undefined' && module.exports) module.exports = api
  else root.__a11yColor = api
})(typeof window !== 'undefined' ? window : globalThis)
