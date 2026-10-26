import { tx, useLanguage } from '@/lib/i18n'
import { Facts, Section } from '@/components/ui/section'
import { ArtifactEncryptionFacts } from './presentation'
import type { ArtifactEncryption } from './types'

/** Format and key facts of one Backup; plain (v1) artifacts say so explicitly instead of hiding the section. */
export function ArtifactEncryptionSection({ encryption }: { encryption: ArtifactEncryption | null | undefined }) {
  useLanguage()
  return <Section title={tx("Şifreleme")} description={encryption ? tx("Dosya Agent'ta şifrelendi; depolama, relay ve replikalar yalnızca şifreli veriyi görür. Backup checksum şifreli dosyaya, kaynak checksum düz veriye aittir.") : tx("Bu dosya şifrelenmeden yazıldı. v1 ve v2 dosyalar birlikte okunur; mevcut dosyalar değiştirilmez.")}>
    <Facts columns={1}><ArtifactEncryptionFacts encryption={encryption} /></Facts>
  </Section>
}
