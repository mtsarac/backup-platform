# Tamamlanan Ticket ve Spec Arşivi

Bu klasör, aktif geliştirme belgelerinden ayrılan ve kaynak dosyalarında `done` olarak kaydedilmiş ticket/spec kümelerini içerir. Dosyaların içerikleri ve mevcut kabul işaretleri korunur; arşivleme yeni bir test veya tamamlanma onayı değildir.

## Arşivlenen çalışmalar

| Çalışma | Spec | Ticketlar | Kaynak durum ve kanıt |
| --- | --- | --- | --- |
| T1 — Agent doğrudan depolama transferi | [spec](t1-direct-transfer/spec.md) | [T1.0](t1-direct-transfer/issues/01-t1-0-feasibility-adr-and-gates.md), [T1.1](t1-direct-transfer/issues/02-t1-1-agent-execution-and-auth.md), [T1.2](t1-direct-transfer/issues/03-t1-2-direct-backup-drive-and-sftp.md), [T1.3](t1-direct-transfer/issues/04-t1-3-direct-restore-repair-lifecycle.md), [T1.4](t1-direct-transfer/issues/05-t1-4-direct-browser-download-breakglass.md), [T1.5](t1-direct-transfer/issues/06-t1-5-relay-cutover-and-cleanup.md) | Spec ve altı ticket `done`. T1.1–T1.5 dosyalarında işaretlenmemiş kabul maddeleri hâlâ bulunuyor; bunlar doğrulanmış sayılmadı veya otomatik işaretlenmedi. |
| Öncelik 5A.1 — Gözlemlenebilirlik çekirdeği | [spec](p5a-observability/spec.md) | [Kapasite sorgulama](p5a-observability/issues/01-hybrid-storage-capacity-probe.md), [Kapasite eşikleri ve bildirimler](p5a-observability/issues/02-capacity-thresholds-and-n1-alerts.md), [Boyut anomalisi](p5a-observability/issues/03-backup-size-anomaly-detection.md), [Sağlık puanlama](p5a-observability/issues/04-composite-backup-health-scoring.md), [Dashboard ve kabul kapanışı](p5a-observability/issues/05-observability-dashboard-and-e2e-closing.md) | Spec ve beş ticket `done`; kabul maddeleri işaretli. Önceki doğrulama kayıtları ilgili ticketların `Verification` bölümlerindedir. |

## Aktif kalan işler

[Roadmap spec](../roadmap/spec.md) ve [roadmap ticketları](../roadmap/issues/) aktif konumlarında kalır. Özellikle:

- Zaman pencereli bant genişliği profilleri, dayanıklı pause/resume ve ağ kesintisi kurtarma aynı aktif spec kapsamındadır.
- İmzalı Agent auto-update, kriptografik audit ve raporlama, tamamlanan 5A.1 çekirdeğinden ayrı olan 5A.2 işleridir. Üst ticketın `done` etiketi bu açık maddelerin tamamlandığı anlamına gelmez.
- S3/Object Lock/SMB ve platform olgunlaştırma için bu çalışma sırasında kod eklenmiş olması, bütünleşik doğrulama ve kabul tamamlanmadan ticket kapanışı sayılmaz.
- Dosya/klasör yedekleme ve geri yükleme çalışması devam etmektedir.

Arşivleme sırasında geçmiş testler yeniden çalıştırılmadı. Buradaki durumlar mevcut kaynak belgelerinden aktarılmıştır; yeni doğrulama sonuçları gibi yorumlanmamalıdır.
