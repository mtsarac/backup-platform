# 03: Öncelik 7 — Faz 15-18: Tam 3-2-1 Policy, S3 Object Storage, Object Lock ve SMB/NAS

**What to build:** Tam 3-2-1 kural motoru (farklı medya türü, off-site doğrulama), S3 uyumlu depolama sağlayıcısı (AWS, MinIO, Wasabi vb.), fidye yazılımına karşı WORM / Object Lock değişmezlik koruması ve yerel ağ için SMB / NAS depolama entegrasyonu.

**Blocked by:** 01: Öncelik 5A, 02: Öncelik 6

**Status:** done

- [x] Faz 15: Tam 3-2-1 kural motoru: en az 3 kopya, 2 farklı ortam/medya sınıfı, 1 off-site doğrulaması
- [x] Faz 16: S3-compatible storage provider (multipart upload, doğrudan Agent execution, presigned/direct transfer)
- [x] Faz 17: Immutable backup: S3 Object Lock (Compliance / Governance modları) ile WORM veri koruması
- [x] Faz 18: SMB / NAS yerel ağ depolama sağlayıcısı ve kimlik doğrulama yönetimi

Gerçek HTTPS MinIO smoke kontrolünde COMPLIANCE ve GOVERNANCE için 17.825.915 baytlık üç parçalı upload, servis checkpoint'leri, byte-for-byte download ve SHA-256 read-back geçti. Bucket Object Lock ve obje retention ayrı imzalı servis çağrılarıyla doğrulandı. Hem provider silmeyi reddetti hem de doğrudan version-specific DELETE WORM korumasına takıldı. Canlı SMB/NAS ve AWS-hosted S3 bu ortamda denenmedi.
