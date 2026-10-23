# Tamamlanan Ticket ve Spec Arşivi

Bu klasör, aktif geliştirme belgelerinden ayrılan ve kaynak dosyalarında `done` olarak kaydedilmiş ticket/spec kümelerini içerir. Dosyaların içerikleri ve mevcut kabul işaretleri korunur; arşivleme yeni bir test veya tamamlanma onayı değildir.

## Arşivlenen çalışmalar

| Çalışma | Spec | Ticketlar | Kaynak durum ve kanıt |
| --- | --- | --- | --- |
| T1 — Agent doğrudan depolama transferi | [spec](t1-direct-transfer/spec.md) | [T1.0](t1-direct-transfer/issues/01-t1-0-feasibility-adr-and-gates.md), [T1.1](t1-direct-transfer/issues/02-t1-1-agent-execution-and-auth.md), [T1.2](t1-direct-transfer/issues/03-t1-2-direct-backup-drive-and-sftp.md), [T1.3](t1-direct-transfer/issues/04-t1-3-direct-restore-repair-lifecycle.md), [T1.4](t1-direct-transfer/issues/05-t1-4-direct-browser-download-breakglass.md), [T1.5](t1-direct-transfer/issues/06-t1-5-relay-cutover-and-cleanup.md) | Spec ve altı ticket `done`. T1.1–T1.5 dosyalarında işaretlenmemiş kabul maddeleri hâlâ bulunuyor; bunlar doğrulanmış sayılmadı veya otomatik işaretlenmedi. |
| Öncelik 5A.1 — Gözlemlenebilirlik çekirdeği | [spec](p5a-observability/spec.md) | [Kapasite sorgulama](p5a-observability/issues/01-hybrid-storage-capacity-probe.md), [Kapasite eşikleri ve bildirimler](p5a-observability/issues/02-capacity-thresholds-and-n1-alerts.md), [Boyut anomalisi](p5a-observability/issues/03-backup-size-anomaly-detection.md), [Sağlık puanlama](p5a-observability/issues/04-composite-backup-health-scoring.md), [Dashboard ve kabul kapanışı](p5a-observability/issues/05-observability-dashboard-and-e2e-closing.md) | Spec ve beş ticket `done`; kabul maddeleri işaretli. Önceki doğrulama kayıtları ilgili ticketların `Verification` bölümlerindedir. |
| Roadmap — Öncelik 5A.2, 6, 7, 8 ve 9 | [spec ve doğrulama](roadmap/spec.md) | [Sekiz ticket](roadmap/issues/) | Kod ve kabul maddeleri tamamlandı. Bu çalışma sırasında gerçek PostgreSQL, MinIO, imzalı güncelleme, Linux paketleme, dosya/UI smoke kontrolleri ve Spec/Standards inceleme bulgularının düzeltmeleri yapıldı. Platforma/harici hizmete bağlı doğrulanmayan alanlar spec ve ilgili ticketlarda açıkça kayıtlıdır. |

## Doğrulama sınırları

Roadmap belgeleri tamamlanmış kod ve çalıştırılmış kontrolleri ayrı kaydeder.
`done`, her işletim sistemi veya canlı servis kombinasyonunun bu makinede
çalıştırıldığı anlamına gelmez. Windows VM erişimi, gerçek snapshot diskleri,
servis kurulum yaşam döngüsü ve canlı harici hesap gerektiren kontrollerin
sınırları [roadmap doğrulama kaydında](roadmap/spec.md#verification) bulunur.

T1 ve 5A.1 geçmiş kayıtları önceki arşivleme sırasında yeniden test edilmemiştir;
o satırlardaki kanıtlar özgün ticket geçmişinden gelir.
