import { Cloud, Database, HardDrive, Server } from 'lucide-react'

const icons = { local: HardDrive, central: Cloud, sftp: Server, 'google-drive': Cloud } as const

/** A plain icon that tells destination kinds apart at a glance; the provider name is always printed next to it. */
export function ProviderIcon({ providerType }: { providerType: string }) {
  const Icon = Object.hasOwn(icons, providerType) ? icons[providerType as keyof typeof icons] : Database
  return <span aria-hidden="true" className="mt-0.5 shrink-0 text-text-muted"><Icon className="size-5" /></span>
}
