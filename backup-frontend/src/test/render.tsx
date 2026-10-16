/* oxlint-disable react/only-export-components -- vitest aliases '@testing-library/react' to this module; it must re-export everything. */
import { createElement, type ComponentType, type ReactElement, type ReactNode } from 'react'
import * as rtl from '@testing-library/react/pure'
import { LanguageProvider } from '@/lib/i18n'

export * from '@testing-library/react/pure'

type Wrapper = ComponentType<{ children: ReactNode }>

// Every rendered tree gets the language context the real app mounts in Providers.
function withLanguage(Inner?: Wrapper): Wrapper {
  return ({ children }) => createElement(LanguageProvider, null, Inner ? createElement(Inner, null, children) : children)
}

export function render(ui: ReactElement, options: rtl.RenderOptions = {}) {
  return rtl.render(ui, { ...options, wrapper: withLanguage(options.wrapper as Wrapper | undefined) })
}

export function renderHook<Result, Props>(callback: (props: Props) => Result, options: rtl.RenderHookOptions<Props> = {}) {
  return rtl.renderHook(callback, { ...options, wrapper: withLanguage(options.wrapper as Wrapper | undefined) })
}
