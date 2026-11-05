import type { ReactNode } from 'react'
import { ShellFrame } from './shell-frame'
import { Sidebar } from './sidebar'
import { TopBar } from './top-bar'

export function AppShell({ children }: { children: ReactNode }) {
  return <ShellFrame Sidebar={Sidebar} TopBar={TopBar}>{children}</ShellFrame>
}
