# 01: Zaman Pencereli Bant Genişliği Profilleri ve Dinamik Hız Belirleme (Faz 2B)

**What to build:** Tenant, Agent ve Job bazlı haftalık zaman pencereli bant genişliği profillerinin (mesai saati kısıtlı, gece sınırsız) tanımlanması, IANA TimeZone ve DST geçişlerine göre dinamik hız sınırının belirlenmesi, REST API ve Web arayüzü ile yönetimi.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] `BandwidthProfile` ve `TransferScheduleWindow` modelleri, EF Core migrasyonu ve veritabanı şeması.
- [x] Profil çözümleme önceliği: `Job > Agent > Tenant` hiyerarşisi ve açık IANA TimeZone desteği.
- [x] `/api/agent/tasks/{attemptId}/upload-policy` endpoint'inin dinamik pencere geçiş zamanına göre `RefreshAfter` süresi dönmesi.
- [x] `UploadPacer` döngüsünün pencere değiştiğinde anlık olarak yeni hız sınırına uyarlanması.
- [x] REST API (`GET/POST/PUT/DELETE /api/bandwidth-profiles`, işe atama) ve Web paneli arayüzü.
- [x] Deterministic unit ve entegrasyon testleri (DST yaz/kış saati geçişi ve pencere değişim anı testleri).

Son Spec düzeltmeleri: kazanan assignment satırı korunur; aynı profil birden fazla scope'a atanırsa scope yanlış satırdan okunmaz. Seçilen profil bütün statik varsayılanların yerine geçer; pencere dışında sınırsızdır. Yalnız seçilen tenant profili tenant fair-share grant'i alır. Profil penceresinde `bytesPerSecond: 0` açık duraklatma demektir; statik limitte sıfır hâlâ geçersizdir. Gerçek UI'da duraklatma seçimi, pasif hız kontrolleri, kaydetme ve masaüstü/mobil görünüm kontrol edildi. `ScheduledTransferPause` migrasyonu ve güncel agent birlikte dağıtılmalıdır.
