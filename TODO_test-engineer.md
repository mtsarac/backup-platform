# TODO_test-engineer.md — Test Suite İncelemesi

Kapsam: `backup-backend/tests/*` (xUnit), `backup-frontend` (Vitest + Testing Library), `.github/workflows/*`.
Yöntem: statik tarama (grep/sayım) + çalıştırma. Kapsam (coverage) aracı repoda **yok**, bu yüzden "kapsanmıyor" iddiaları isim-referansı heuristiğidir `[INFERENCE]`; gerçek yüzde ölçülmedi.

## Context

| Proje | Dosya | LOC | `[Fact]/[Theory]` | Çalıştırma sonucu (bu oturum) |
|---|---|---|---|---|
| unit-tests | 141 | 35k | 2084 | **4100 geçti / 0 hata / 0 skip** (29 s) — `-p:TreatWarningsAsErrors=false` ile, bkz. TE-ITEM-1.1 |
| agent-tests | 51 | 9.4k | 358 | çalıştırılmadı |
| integration-tests | 173 | 46.6k | 1393 | çalıştırılmadı (PostgreSQL/MSSQL/Docker gerekir) |
| architecture-tests | 5 | 1.2k | 69 | çalıştırılmadı |
| windows-tests | 7 | 1.4k | (özel attribute) | çalıştırılmadı (Windows gerekir) |
| backup-frontend | 74 | — | 674 | **73 dosya / 674 test geçti** (39 s) |

Araçlar: xUnit 2.9.3, `FakeTimeProvider`, elle yazılmış fake/harness (Moq/NSubstitute/FluentAssertions yok), WebApplicationFactory, PostgreSQL+MSSQL fixture; frontend Vitest 5 + RTL + user-event + jsdom.
Olumlu: 29 dosyada deterministik zaman, `Wait.UntilAsync` ile polling, gizli bilgi sızıntısı testleri (`SecretLeakageTests`, `RestoreSecretLeakageTests`), mimari sınır testleri, tenant-isolation testleri, `Skip` nedenleri açık yazılı, snapshot/`.only`/`.skip` yok.

## Bulgular (öncelik sırasıyla)

### P0

- [ ] **TE-ITEM-1.1 [Çalışma ağacı Release build'i kırıyor]**
  - **Bulgu**: `git status`'ta commit edilmemiş `src/hosts/api/Security/SecurityApplicationBuilderExtensions.cs` `ForwardedHeadersOptions.KnownNetworks` kullanıyor → `ASPDEPR005` (obsolete). `Directory.Build.props` `CI=true` iken `TreatWarningsAsErrors` açıyor; bu oturumda `CI=true` olduğundan `dotnet test tests/unit-tests` **derlenmedi**. CI'daki `-warnaserror` adımı da kırılır.
  - **Düzeltme**: `KnownNetworks.Clear()` → `KnownIPNetworks.Clear()`.
  - **Assertion**: `dotnet build BackupPlatform.sln -c Release -warnaserror` temiz.

- [ ] **TE-ITEM-1.2 [Forwarded-headers değişikliği için test yok]**
  - **Bulgu**: Aynı değişiklik `KnownProxies`/`KnownNetworks`'ü temizleyerek **her** istemciden `X-Forwarded-For/Proto` kabul ediyor. Testlerde `ForwardedHeaders`/`X-Forwarded` geçen dosya yok (grep: 0). Rate-limit (`AgentRateLimitIntegrationTests`) ve audit IP'si bu başlıkla sahtelenebilir `[INFERENCE: istemci IP'sinin bu yollarda kullanıldığı doğrulanmadı]`.
  - **Setup**: `integration-tests` içinde `WebApplicationFactory`; `X-Forwarded-For: 203.0.113.9` ile istek.
  - **Assertions**: (a) güvenilir olmayan uzak adres başlığı gönderince `RemoteIpAddress` değişmez (ya da amaçlanan davranış açıkça sabitlenir); (b) rate-limit anahtarı spoof edilen IP ile aşılamaz; (c) `X-Forwarded-Proto` yalnız yapılandırılmış proxy'den kabul edilir. Önce amaçlanan güven modeli netleştirilmeli (karar kullanıcıda).

- [ ] **TE-ITEM-2.1 [PR CI'ı integration + Windows testlerini çalıştırmıyor]**
  - **Bulgu**: `integration.yml` ve `windows.yml` yalnızca `workflow_dispatch`. `ci.yml` (PR/push) sadece unit + architecture + agent(−`ConnectivityServiceTests`) + frontend çalıştırıyor. Toplam test kodunun ~%50'si (46.6k LOC, 1393 test) — authz, tenant isolation, audit güvenliği, lease/fencing, restore/retention, EF migrasyonları — hiçbir PR'da kapı değil. `release.yml` de yalnızca `ci.yml`'e bağlı; yayın şu an duraklatılmış.
  - **Karar**: en az PostgreSQL işini (`integration.yml` → `postgres` job'ı, ~45 dk limit) `push: main` + gecelik `schedule` ile otomatik çalıştır; MSSQL/Agent.Host smoke ve Windows'u gecelik/haftalık bırak. PR'da yol filtresi (`paths: backup-backend/src/**`) ile PostgreSQL alt kümesi opsiyonel.
  - **Patch (`.github/workflows/integration.yml`)**:
    ```diff
     on:
    -  workflow_dispatch:
    +  workflow_dispatch:
    +  push:
    +    branches: [main]
    +  schedule:
    +    - cron: "0 2 * * *"
    ```
  - **Assertion**: main'e push'ta `postgres` job'ı koşar ve `trx_report.py --require-passed` ile kapı olur.

### P1

- [ ] **TE-ITEM-3.1 [Kapsam ve mutasyon ölçümü yok]**
  - **Bulgu**: `coverlet`/`Stryker`/vitest `coverage` hiçbir csproj/props/package.json/`vitest.config.ts`'te yok; prompt hedefleri (≥%80 kritik yol, mutasyon >%75) ölçülemiyor.
  - **Yapılacak**: backend için `coverlet.collector` (Directory.Packages.props'a ekle) + `dotnet test --collect:"XPlat Code Coverage"`; frontend için `@vitest/coverage-v8` + `test.coverage.thresholds`. Önce **ölçüm raporu**, eşik sonra (mevcut seviyenin hemen altı, sonra yukarı sıkılaştır). Stryker.NET'i yalnızca `Domain`, `Application`, `agent/core` (retry, lease, quota, retention) için hedefle.
  - **Assertion**: CI artefaktı olarak cobertura/lcov; eşik altında job kırılır.

- [ ] **TE-ITEM-3.2 [Doğrudan birim testi olmayan, yüksek riskli tipler]** `[INFERENCE — ad referansı yok; HTTP/integration üzerinden dolaylı kapsanıyor olabilir, bunlar PR'da koşmuyor (TE-ITEM-2.1)]`
  - Test projelerinde adı hiç geçmeyen tipler (src LOC): `DatabaseBackupExecution` (773, agent/core), `FileSystemStorageProvider` (630, ancak `Local` alt sınıfı üzerinden test ediliyor olabilir), `SqlServerRestoreExecutor` (411), `PostgreSqlRestoreExecutor` (300), `TenantUsageEngine` (311) + `StorageUsageProjection` (178) (kota/hesaplama), `SupportAccessService` (123, güvenlik), `AgentCredentialService` (143), `AgentAuthentication` (145), `PlatformAdministrationService` (101), `AgentDownloadSpool` (129), `GoogleDriveStagingDirectory` (345), `PostgreSqlFullBackupExecutor` (165), `SqlServerStagingProbe` (213).
  - **Yapılacak**: her biri için önce `grep` ile dolaylı kapsama doğrula; kapsanmayan davranış için birim test ekle. Öncelik: `TenantUsageEngine` (para/kota doğruluğu — sınır değerler, boş tenant, çoklu replica), `SupportAccessService` (süre dolumu, iptal, tenant sınırı), `AgentCredentialService` (rotasyon, reddedilen/pending kimlik).
  - **Assertions**: kota hesaplamasında sınır (`limit`, `limit±1`, 0), yetkisiz tenant → reddedilir, süresi dolmuş oturum → reddedilir.

- [ ] **TE-ITEM-4.1 [`ConnectivityServiceTests` CI'dan dışlı]**
  - **Bulgu**: `ci.yml` bunu `--filter "FullyQualifiedName!~ConnectivityServiceTests"` ile atlıyor (844 satır); "timing" gerekçesi yorumda. Hiçbir iş bunu çalıştırmıyor (`integration.yml`'de agent-tests yok `[INFERENCE: ilk 100 satırı okudum; tamamını doğrulamadım]`).
  - **Yapılacak**: gerçek zamanlı `Task.Delay`'leri `FakeTimeProvider`/`DeterministicTimeProvider` ile değiştirip dışlamayı kaldır; mümkün değilse gecelik işe taşı.
  - **Assertion**: dosya filtre olmadan 20× üst üste geçer.

### P2

- [ ] **TE-ITEM-5.1 [Gerçek zamana bağlı testler (flakiness riski)]**
  - **Bulgu**: tests altında `Task.Delay` 88, `Thread.Sleep` 3 (`TcpForwarder.cs:50`, windows-tests 2), duvar saati assertion'ları: `UploadBandwidthApiIntegrationTests.cs:207-233` (`watch.Elapsed >= floor - 0.1` ve `< floor*2+1`), `SqlServerConnectionTests.cs:200` (`< 5 s`), `MsSqlBackupExecutionIntegrationTests.cs:415` (`< 2 s`). Sınırlı: `Wait.UntilAsync` 15 s timeout + 20 ms polling (kabul edilebilir).
  - **Yapılacak**: pacing/limiter testlerini `FakeTimeProvider` ile sür (`TransferRateLimiterTests` zaten benzer yoldadır — örnek al); `Task.Delay(30)` gibi "iş yapıyormuş gibi" gecikmeleri `TaskCompletionSource` ile değiştir (`DiscoveryScannerTests.cs:559`); `< 2 s` gibi sıkı üst sınırları gevşet veya iptal-sinyali gözlemine çevir.
  - **Assertion**: `dotnet test --filter ... -- xUnit.MaxParallelThreads=8` ile 50× tekrar, 0 hata.

- [ ] **TE-ITEM-5.2 [Doğrudan `DateTime.UtcNow` kullanımı]**
  - **Bulgu**: 226 kullanım; en yoğun `ManualReplicaRepairIntegrationTests.cs` (26), `PipelineHarness.cs` (12), `SubscriptionInvariantIntegrationTests.cs` (11), `RestorePipelineHarness.cs` (9).
  - **Yapılacak**: harness'larda sabit `T0` + `FakeTimeProvider`; test verisi zamanını `T0`'dan türet (gün sınırı/DST/ay sonu kayması riskini kaldırır — özellikle abonelik/retention testleri).

- [ ] **TE-ITEM-5.3 [Zayıf assertion biçimleri]**
  - **Bulgu**: `Assert.True(` 1488, bunların 102'si `==`/`!=` karşılaştırması (`ProtocolVersionTests.cs:36`, `CapabilityTests.cs:53` — kasıtlı operatör testi olabilir, ama diğerlerinde başarısızlık mesajı bilgi vermez) ve 7'si `Contains/StartsWith`. `Assert.NotNull` 356.
  - **Yapılacak**: operatör-dışı olanları `Assert.Equal`/`Assert.Contains`/`Assert.Single`'a çevir. Mekanik; `ast_edit` ile yapılabilir. `Assert.NotNull` sonrası değer kullanılmıyorsa gerçek davranış assertion'ı ekle.

- [ ] **TE-ITEM-5.4 [Dosya boyutları / okunabilirlik]**
  - **Bulgu**: 1574 (`ManualReplicaRepairIntegrationTests`), 1161 (`RestoreOperationTests`), 1148 (`BackupRetryPersistenceTests`), 1110 (`DatabaseRestorePipelineTests`), 1099 (`RestoreServiceTests`). Fixture/harness paylaşımı iyi (`Support/*Harness.cs`), ama tek dosyada çok senaryo.
  - **Yapılacak**: konuya göre böl (başarı/iptal/sahiplik/yeniden deneme); davranış değiştirme. Düşük öncelik — "sırf satır sayısı için" bölme (AGENTS.md).

- [ ] **TE-ITEM-6.1 [Windows testleri çoğunlukla atlanır]**
  - **Bulgu**: yalnız `windows.yml` (dispatch) koşuyor; `WindowsAdminFact` testleri yükseltilmiş oturum yoksa `SKIPPED, NOT PASSED` (`Support.cs:34,50`). Linux CI'da Windows kodu (`agent/platform-windows`, 669 LOC; `hosts/agent/WindowsEnrollmentWorker`, 115) için hiç doğrulama yok.
  - **Yapılacak**: `windows.yml`'e haftalık `schedule`; GitHub `windows-latest` runner admin olduğundan `WindowsAdminFact`'in gerçekten koştuğunu `trx_report.py`'de **skipped sayısı = 0** olarak doğrula.
  - `tests/zz-tmp/` boş dizin (git'te izlenmiyor, yerelde kalmış) — sil.

### Frontend

- [ ] **TE-ITEM-7.1 [`fireEvent` ağırlığı]**
  - **Bulgu**: 308 `fireEvent` vs 233 `userEvent`. `fireEvent` gerçek kullanıcı akışını (focus, pointer, klavye) simüle etmez; Base UI bileşenlerinde gerçek hataları gizleyebilir.
  - **Yapılacak**: `fireEvent.click/change` → `await userEvent.setup()` ile değiştir (dosya dosya; önce form/dialog testleri). `fireEvent` yalnız userEvent'in desteklemediği olaylarda kalsın.

- [ ] **TE-ITEM-7.2 [Erişilebilirlik testi yok]**
  - **Bulgu**: `axe` referansı 0; `a11y-toolbar.test.tsx` yalnız tercihleri test ediyor. `getByTestId` 21 kullanım.
  - **Yapılacak**: `vitest-axe` ile kritik sayfalara (login, wizard, dialoglar, tablolar) tek `expect(await axe(container)).toHaveNoViolations()`; `getByTestId` → `getByRole`/`getByLabelText` (21 yer).

- [ ] **TE-ITEM-7.3 [Test edilmeyen modüller]**
  - **Bulgu** (dosya adı eşleşmesi; ilgili feature `*.test.tsx` dosyası dolaylı kapsıyor olabilir `[INFERENCE]`): `features/*/hooks.ts` (agents, artifacts, discovery, notifications, platform, platform/storage, platform/notifications, platform/plans, restores, storage, support-access, upload-bandwidth) ve `features/{agents,artifacts,backups,restores,storage,platform/storage,platform/notifications}/api.ts`; `lib/query-client.ts`, `lib/routes.ts`, `lib/a11y-prefs.ts`; sayfalar `login-page`, `tenant-select-page`, `restore-wizard-page`, `restore-detail-page`, `artifact-detail-page`, `clients-page`, `platform-audit-page`, `platform-quota-policies-page`.
  - **Yapılacak**: önce kapsam raporu (TE-ITEM-3.1) ile gerçek boşlukları belirle. Öncelik: `restore-wizard-page` (veri kaybı riski olan akış), `login-page`/`tenant-select-page` (tenant izolasyonu), `routes.ts` (yetki yönlendirmesi).

- [ ] **TE-ITEM-7.4 [`vi.mock` yoğunluğu]**
  - **Bulgu**: 102 `vi.mock(`. API katmanını mock'lamak sınırda doğru; ancak hook/bileşen iç modüllerini mock'layan testler refactor'da kırılgan.
  - **Yapılacak**: `ky` istemcisi/`fetch` sınırında (MSW veya tek paylaşılan `fetch` stub'ı) mock'a geçmeyi değerlendir; iç modül mock'larını kademeli azalt. Düşük öncelik.

- [ ] **TE-ITEM-7.5 [E2E yok]**
  - **Bulgu**: Playwright/Cypress yok; kullanıcı yolculukları (giriş → job oluştur → yedek → restore) yalnız parça parça test ediliyor. Backend'de `BackupPipelineSmokeTests`/`MsSql*AgentSmokeTests` var ama UI ile bağlı değil.
  - **Yapılacak**: 2–3 Playwright smoke (login+tenant seçimi, backup job sihirbazı, restore sihirbazı) docker-compose ortamına karşı, gecelik. Pyramid'in tepesi, minimum tut.

## Test Strategy Plan

- [ ] **TE-PLAN-1.1 [Piramit]**
  - **Scope**: Birim (domain/application/agent core, frontend saf fonksiyon+hook) → entegrasyon (HTTP + PostgreSQL; zaten güçlü) → az sayıda E2E/smoke.
  - **Rationale**: Mevcut dağılım (4100 birim / 1393 entegrasyon / 358 agent) piramide uygun; sorun seviye değil **hangi seviyenin PR'da koştuğu** (TE-ITEM-2.1).
  - **Coverage Target**: ölçüm sonrası — Domain/Application/agent-core ≥%85 satır, güvenlik/tenant/kota yolları ≥%90 dal; frontend `features/**` ≥%80. Mutasyon ≥%75 yalnız seçili projelerde.

## Proposed Code Changes

- [ ] `SecurityApplicationBuilderExtensions.cs`: `KnownNetworks` → `KnownIPNetworks` (TE-ITEM-1.1).
- [ ] `.github/workflows/integration.yml`: `push: main` + `schedule` (TE-ITEM-2.1, patch yukarıda).
- [ ] `Directory.Packages.props`: `coverlet.collector`; frontend `@vitest/coverage-v8` (TE-ITEM-3.1).
- [ ] Mekanik temizlik: `Assert.True(a == b)` (TE-ITEM-5.3), `fireEvent` (TE-ITEM-7.1).

## Commands

```bash
# Backend (CI=true ortamında uyarı hatası olmasın diye, yalnız yerelde)
cd backup-backend
dotnet test tests/unit-tests -c Release -p:TreatWarningsAsErrors=false
dotnet test tests/architecture-tests -c Release
dotnet test tests/agent-tests -c Release --filter "FullyQualifiedName!~ConnectivityServiceTests" -- xUnit.MaxParallelThreads=2
# Entegrasyon (PostgreSQL gerekli; .github/scripts/start-postgres.sh)
dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke" -- xUnit.MaxParallelThreads=2
# Kapsam (TE-ITEM-3.1 sonrası)
dotnet test tests/unit-tests -c Release --collect:"XPlat Code Coverage"

# Frontend
cd backup-frontend
bun run typecheck && bun run lint && bun run test
```

## Quality Assurance Checklist

- [x] Test adları davranış cümlesi (`...IsNotFoundAndTheTesterIsNotRun`), tutarlı.
- [x] Mock framework yok; elle fake'ler → over-mocking riski düşük.
- [x] Gizli bilgi sızıntısı, tenant izolasyonu, mimari sınır testleri mevcut.
- [ ] Kritik yollar PR'da koşuyor → **hayır** (TE-ITEM-2.1).
- [ ] Kapsam/mutasyon eşikleri CI kapısı → **hayır** (TE-ITEM-3.1).
- [ ] Tüm testler deterministik → kısmen (TE-ITEM-5.1/5.2).
