import { tx, useLanguage } from '@/lib/i18n'
import { TechnicalDetails, type TechnicalItem } from '@/components/ui/technical-details'
import { formatDateTime } from '@/lib/format'
import { CapabilityList } from './capabilities'
import type { AgentDetail } from './types'

/** Every identifier, version and lifecycle value of the computer; values the computer never reported are left out. */
function technicalItems(agent: AgentDetail): TechnicalItem[] {
  const date = (value: string | null | undefined) => value ? formatDateTime(value) : null
  return ([
    [tx("Bilgisayar kimliği"), agent.agentId],
    [tx("Platform"), agent.platform],
    [tx("İşletim sistemi"), agent.osDescription],
    [tx("Yedekleme programı sürümü"), agent.agentVersion],
    [tx("Protokol sürümü"), agent.protocolVersion],
    [tx("Çalışma ortamı"), agent.runtimeVersion],
    [tx("Yaşam döngüsü"), agent.status],
    [tx("Çalışma durumu"), agent.operationalStatus],
    [tx("Kayıt zamanı"), date(agent.enrolledAt)],
    [tx("Son durum sinyali"), date(agent.lastHeartbeatAt)],
    [tx("Son bağlantı"), date(agent.lastConnectedAt)],
    [tx("Yeteneklerin bildirilme zamanı"), date(agent.capabilitiesReportedAt)],
    [tx("Bildirilen yetenek revizyonu"), agent.capabilityRevision],
    [tx("Kaydedilen yetenek revizyonu"), agent.capabilitiesRevision],
  ] as const).flatMap(([label, value]) => value ? [{ label, value }] : [])
}

/** The one collapsed "Teknik ayrıntılar" of a backup computer, shared by the tenant page and the platform page. */
export function AgentTechnicalDetails({ agent }: { agent: AgentDetail }) {
  useLanguage()
  return <TechnicalDetails items={technicalItems(agent)}>
    <section className="space-y-3">
      <h3 className="text-sm font-semibold text-text-heading">{tx("Bildirilen yetenekler")}</h3>
      {agent.capabilities.length ? <>
        <CapabilityList capabilities={agent.capabilities} />
        <p className="text-sm text-text-muted">{tx("Bilgisayar düzeyindeki destek, veritabanı sunucusunun gerçek desteğini garanti etmez; her yedekten önce ön kontrol yapılır.")}</p>
      </> : <p className="text-text-muted">{tx("Yetenekler henüz bildirilmedi.")}</p>}
    </section>
  </TechnicalDetails>
}
