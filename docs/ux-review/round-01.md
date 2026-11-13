# Backup Platform — UI/UX İnceleme, Tur 1

**Tarih:** 13 Kasım 2026  
**Durum:** Bulgular kayıt altına alındı; düzeltmeler kullanıcı tarafından henüz doğrulanmadı.  
**Ortam:** İzole `bpreview` arayüzü `https://localhost:5183`, Chromium, güncel `main` temelli review worktree.  
**Kapsam:** PlatformAdmin ve UserAdmin arayüzleri; ilk manuel değerlendirme.

> Bu belge kullanıcı gözlemlerini ve kabul kriterlerini kaydeder. 500 hatalarının kesin kök nedeni henüz doğrulanmadı. Review ortamında gerçek müşteri verisi kullanılmayacak. Epic #99 kapalı kalacak; bu inceleme ayrı bir iyileştirme çalışmasıdır.

## Takip tablosu

| ID | Öncelik | Alan | Gözlem / istek | Durum |
|---|---|---|---|---|
| UX-01 | P0 | Passkey ile giriş | İlk giriş ekranındaki Passkey düğmesi HTTP 500 döndürüyor. | Kullanıcı kontrolünde |
| UX-02 | P0 | Passkey ekleme | Geçiş anahtarı kaydı HTTP 500 döndürüyor. | Kullanıcı kontrolünde |
| UX-03 | P1 | Hata sunumu | Hata ayrıntıları sağ çekmecede açılıyor; ortak, daha anlaşılır hata sistemi gerekli. | Kullanıcı kontrolünde |
| UX-04 | P1 | Teknik ayrıntılar | `TechnicalDetails` sağ çekmecesi yerine erişilebilir, ortalanmış modal/popup gerekli. | Kullanıcı kontrolünde |
| UX-05 | P1 | Kullanıcı ayarları | Üst çubuktaki Oturumlarım / Güvenlik / Geçiş anahtarları / kullanıcı adı kalabalığı yerine sidebar altında kullanıcı adı + dişli ikonuyla Ayarlar girişi gerekli. | Kullanıcı kontrolünde |
| UX-06 | P1 | Yeni yedekleme işi | Otomatik keşif akışında bilgisayar seçimi yineleniyor; otomatik keşif öne çıkmalı. | Kullanıcı kontrolünde |
| UX-07 | P1 | Yedek dosyaları | “Geri yüklenebilir mi?” gibi soru-cevap metni yerine doğrudan, kısa durum bilgisi isteniyor. | Kullanıcı kontrolünde |
| UX-08 | P1 | Yedek dosyaları | Listede detay açılmadan hangi makine/veritabanı/dosya kaynağının yedeklendiği anlaşılmıyor. | Kullanıcı kontrolünde |
| UX-09 | P1 | Yedeklemeler navigasyonu | Sol menüdeki dört link, aynı dört üst sekmeyi yineliyor. | Kullanıcı kontrolünde |

**Durum akışı:** Açık → İncelendi → Uygulandı → Kullanıcı kontrolünde → Onaylandı. Başarısız kullanıcı kontrolünde tekrar İncelendi'ye dönülür.

## UX-01 — Passkey ile giriş HTTP 500

- **Adımlar:** Chromium'da giriş ekranını aç → Passkey ile giriş düğmesine tıkla.
- **Mevcut:** “Sunucu isteği tamamlayamadı. Daha sonra tekrar deneyin.”; teknik kod 500.
- **Beklenen:** Sunucu başarıyla WebAuthn challenge/options döndürmeli; Chromium kullanılabilir geçiş anahtarını istemeli. Kayıtlı anahtar yoksa veya işlem iptal edildiyse uygun ve anlaşılır mesaj gösterilmeli; beklenen kullanıcı hataları 500 olmamalı.
- **Teşhis:** Tam olarak hangi istek/endpoint'in 500 ürettiğini Network + API loglarıyla belirle. `Authentication:Passkeys:RelyingPartyId` ve `Authentication:Passkeys:Origins` yapılandırmasını, `https://localhost:5183` origin eşleşmesini, RP ID / hostname ayrımını, HTTPS ve reverse-proxy header'larını, WebAuthn challenge/session/CSRF sürecini incele. Bunlar olası alanlar, kanıtlanmış nedenler değil.
- **Test:** Chromium üzerinde kayıtlı passkey ile başarılı giriş, olmayan credential, kullanıcı iptali, yanlış origin/RP ID ve beklenen kontrollü hata durumları.
- **Kabul:** Review ortamında Passkey giriş akışı 500 vermiyor ve geçerli credential ile çalışıyor.

## UX-02 — Geçiş anahtarı ekleme HTTP 500

- **Adımlar:** Oturum aç → Geçiş anahtarları → Geçiş anahtarı ekle.
- **Mevcut:** HTTP 500 ve aynı çekmece biçimindeki hata ayrıntıları.
- **Beklenen:** Başarılı kayıt, listede yeni credential görünmesi, uygun doğrulama / recovery-code davranışı. Kullanıcı iptali, cihaz desteği olmaması ve şifre doğrulama hataları kontrollü gösterilmeli.
- **Teşhis:** Registration options, `navigator.credentials.create`, registration verification adımlarını ayrı ayrı gözle; RP ID, allowed origins, HTTPS, DB migration, challenge persistence ve doğrulama kontrollerini araştır.
- **Test:** Gerçek Chromium + mümkünse sanal WebAuthn authenticator ile registration ve login round-trip; hatalı/iptal senaryoları.
- **Kabul:** Review ortamında credential eklenebiliyor ve eklenen credential ile giriş yapılabiliyor; beklenen durumlarda 500 yok.

## UX-03 — Hata raporlama sistemi

- **İstek:** Hata durumlarında sağdan gelen çekmece kullanılmasın. İşlemi kesintiye uğratmadan sorunu anlatan, bağlama uygun ortak hata tasarımı kullanılsın.
- **Önerilen kural:** Form alanı hatası ilgili alanın altında; sayfa/işlem engeli için sayfa içi `ErrorNotice`; kısa işlemler için gerektiğinde ölçülü toast. Teknik teşhis isteyen kullanıcı `Teknik ayrıntılar` ile modal açabilir. Her hatada modalı otomatik açma.
- **Mesaj içeriği:** Ne başarısız oldu, etkisi, mümkünse bir sonraki adım, uygun `Tekrar dene`. 500 ve ham backend kodları ana mesaj yerine teknik ayrıntıda gösterilir. Korelasyon/request ID varsa kopyalanabilir; token, şifre, secret ve stack trace son kullanıcıya sızdırılmaz.
- **Kabul:** Hata göstermek sayfanın yerleşimini bozmaz; üst üste tekrar eden bildirim oluşmaz; klavye ve ekran okuyucu desteklenir; TR/EN çeviriler uyumludur.

## UX-04 — Teknik ayrıntılar: çekmece yerine modal

- **Mevcut:** Ortak `backup-frontend/src/components/ui/technical-details.tsx` default olarak sağ taraftan açılan geniş bir panel (`TechnicalDrawer`) kullanıyor. Hata alanları da `ErrorNotice` üzerinden bu bileşeni tüketiyor.
- **İstek:** Default mod **merkezde açılan modal/dialog** olsun. Uzun değerler okunabilir ve kopyalanabilir; arama, `Tümünü kopyala`, tek alan kopyalama korunabilir.
- **Davranış:** Escape ile kapanma, odak tuzağı, açan kontrole odak dönüşü, overlay kapanması ve ekran okuyucu başlığı; küçük ekranda uygun full-screen modal. Modal açıkken arka plan scroll'u engellensin.
- **İstisna:** Zaten modal içindeki teknik bilgiler için iç içe ikinci modal açmak yerine mevcut `accordion` kullanımını koru.
- **Kabul:** Ekranların hiçbirinde ortak teknik ayrıntı trigger'ı sağ çekmece açmaz; tasarım desktop/mobile ve dark/light/high-contrast testlerini geçer.

## UX-05 — Kullanıcı ayarları ve sol menü altlığı

- **Mevcut:** `backup-frontend/src/components/layout/top-bar.tsx` üst çubukta `/sessions`, `/security`, `/passkeys`, kullanıcı adı ve çıkış eylemlerini ayrı ayrı sunuyor.
- **İstek:** Tenant ve PlatformAdmin sidebar'larının en altına sabit **“Kullanıcı adı  ⚙”** hesabım/ayarlar girişi ekle. Hesap sayfasında Oturumlarım, Güvenlik, Geçiş anahtarları ve hesap/profil bilgilerini tek çatı altında sekmeler veya alt navigasyonla düzenle. Gerekliyse Çıkış eylemini burada sun.
- **Kabul:** Üst çubuktaki tekrarlar kalkar; bildirim, dil, görünüm ve tenant seçimi korunur. Mevcut `/sessions`, `/security`, `/passkeys` URL'leri deep-link/redirect ile çalışmayı sürdürür. Tenant ve PlatformAdmin arayüzlerinde aynı davranış vardır; mobil sidebar erişilebilir.

## UX-06 — Yeni yedekleme işi sihirbazı / keşif

- **Mevcut gözlem:** Otomatik keşif yolundan gelen kullanıcı aynı bilgisayarı yeniden seçiyor.
- **İstek:** Başlangıçta “Otomatik keşiften seç” ana/öncelikli yol; “Elle yapılandır” ikincil yol. Keşifte bilgisayar, instance ve veritabanı seçimi varsa wizard'a taşınmalı; kullanıcı tekrar seçmemeli. Seçilen kaynağı özet olarak göster; isterse `Değiştir` ile değiştirebilsin.
- **Teknik not:** `backup-job-wizard-page.tsx` zaten `?discoveryInstance=...&database=...` prefill verilerini okuyabiliyor; kaybolan seçim veya yinelenen UI kontrolünün kök nedenini gerçek akışta incele. Mevcut seçim, tenant izolasyonu ve eski URL'ler korunmalı.
- **Kabul:** Keşif → seçim → yeni iş sihirbazı akışında her kaynak bilgisi normal durumda yalnızca bir kez istenir; manuel yol erişilebilir kalır.

## UX-07 — “Geri yüklenebilir mi?” soru-cevap dilini kaldır

- **İstek:** `Geri yükleme durumu`, `Geri yüklemeye hazır`, `Uygun değil`, `Doğrulanıyor` gibi kısa ve doğrudan durum ifadeleri kullan. Duruma ilişkin neden ancak gerekli olduğu yerde küçük destek metni veya ayrıntıda gösterilsin.
- **Kabul:** Yedek dosyaları listesi taranabilir; belirsiz durum kesin “hazır” gibi sunulmaz. Erişilebilirlikte renk tek belirteç olmaz; İngilizce karşılıklar da doğru olur.

## UX-08 — Yedeğin kaynağını listede göster

- **İstek:** Yedek dosyaları satırında, detaya girmeden en az **kaynak türü**, **veritabanı adı veya dosya yolu/kapsamı**, **kaynak bilgisayar/sunucu**, **oluşturulma zamanı**, **boyut** ve **indirme/restore kullanılabilirlik durumu** okunabilmeli. Gerekirse birincil başlık + ikincil meta satırı ve dar ekran kart düzeni kullan.
- **Teknik sınır:** Bilgi API'de yoksa mevcut ilişkilerden güvenli biçimde çıkar; yokken uydurup gösterme. Uzun path ve adlar kontrollü kısaltılsın; tam değer erişilebilir olsun. Tenant izolasyonu korunmalı.
- **Kabul:** Kullanıcı yedeği hangi kaynaktan alındığını ayrıntıyı açmadan ayırt edebilir; mobilde yatay taşma olmaz.

## UX-09 — Yinelenen yedekleme navigasyonu

- **Mevcut:** `sidebar.tsx` dört ayrı sidebar linki; `backup-center-layout.tsx` aynı dört route tabını içeriyor.
- **İstek:** Sidebar'da tek **Yedeklemeler** navigasyon girişi, içeride dört sekme: `Yedekleme işleri`, `Yedekleme denemeleri`, `Yedek dosyaları`, `Geri yüklemeler`. `Paylaşımlarım` ayrı bir sidebar eylemi olarak kalabilir. Sidebar ve tabların aynı sayfa isimlerini ikinci kez göstermesi engellensin.
- **Kabul:** Eski route/deep link/bookmark'lar bozulmaz; aktif yedekleme alt sekmesi doğru vurgulanır; mobil navigasyon da açık ve erişilebilir kalır.

## Uygulama ve kabul protokolü

1. P0 (UX-01, UX-02) sorunlarını root-cause ve gerçek Chromium sonuçlarıyla çöz.
2. Ortak bileşenleri (UX-03, UX-04) tek yerden düzelt; benzer ekranları regresyon açısından tara.
3. Navigasyon ve hesap ayarlarını (UX-05, UX-09) düzenle.
4. Keşif akışı ve artifact listelerini (UX-06, UX-07, UX-08) düzelt.
5. `typecheck`, lint, test, build ve ilgili backend/integration testlerini çalıştır; gerçek Chromium kullanarak görsel/işlevsel doğrulama yap.
6. Öncesi/sonrası ekran görüntüleri, test kanıtları ve açık kalan konularla kullanıcı onayına sun. **Kullanıcı onayı olmadan main'e merge etme.**

**Review ortamı korunacak:** `bpreview` Docker projesini, örnek hesapları ve verileri silme/sıfırlama. Bağımsız diğer worktree ve #60 release çalışmalarına dokunma. Değişiklikleri ayrı branch/worktree içinde uygula ve güvenli biçimde preview'a yansıt. Uygulama davranışını değiştiriyorsan frontend yanında gerekli backend doğrulama ve testlerini yap.

## Uygulama kanıtı (kullanıcı onayı bekleniyor)

- UX-01/02: API logları `/api/auth/passkeys/login/options` ve `/api/auth/passkeys/register/options` için `Passkey relying party ID and origins must be configured` istisnasını gösterdi. Preview için RP ID `localhost`, origin `https://localhost:5183` olarak açıkça yapılandırıldı; güvenilir nginx proxy ayarı korundu. Sanal Chromium authenticator ile kayıt, listede görünme, çıkış ve passkey ile yeniden giriş yapıldı (`/api/me` 200). Kullanılmış sanal anahtar test sonrası demo hesabından kaldırıldı.
- Backend PasskeyIntegrationTests: 21 başarılı (yapılandırma yokken 503 regresyonu dahil). Frontend kapsamlı test sonucu aşağıda tamamlandıktan sonra kaydedilir.
- UX-03/04: ortak teknik ayrıntılar merkezî dialog; mevcut dialog içinde accordion. Önce görüntüsü `screens/ux-03-04-before-drawer.webp`.
- UX-05/09: hesap alanı ve tek yedeklemeler menüsü uygulandı; eski hesap URL'leri yönlendirilir. Önce görüntüleri `screens/ux-05-before-topbar.webp`, `screens/ux-09-before-sidebar.webp`.
- UX-06: keşif verileri wizard state içinde taşınır, manuel yol korunur. Önce görüntüsü `screens/ux-06-before-wizard.webp`. Gerçek keşif akışı doğrulaması devam ediyor.
- UX-07/08: durum etiketleri ve kaynak bilgileri; dosya kapsamı çalıştırmanın snapshot'ından API'ye eklenmiştir. Önce görüntüsü `screens/ux-07-08-before-artifacts.webp`.
- Test edilmiş davranışlar ve eksikler tarayıcı doğrulaması tamamlandıktan sonra güncellenir; henüz kullanıcı onayı yoktur.


### Doğrulama sonuçları

| Madde | Durum | Kanıt / sınırlama |
|---|---|---|
| UX-01 | Kullanıcı kontrolünde | Preview yapılandırması ile Chromium sanal authenticator üzerinden oturumu kapatıp passkey ile giriş: `/api/me` 200. Orijinal 500 API logları yukarıda. |
| UX-02 | Kullanıcı kontrolünde | Güncel preview'da sanal anahtar kaydı ve listesi doğrulandı; test anahtarı hesap erişimini korumak için kaldırıldı (204). İptal ve desteklenmeyen cihaz mesajları frontend testlerinde. |
| UX-03 | Kullanıcı kontrolünde | Hata inline, teknik bilgi isteğe bağlı; `notice.test.tsx` ve paylaşılan bileşen testleri. |
| UX-04 | Kullanıcı kontrolünde | Chromium: dialog açıldı, Escape ile kapandı, odak tetikleyiciye döndü; 375×812'de dialog viewport'a sığıyor. Arama/kopyalama bileşen testleri. |
| UX-05 | Kullanıcı kontrolünde | Tenant hesap linki ve `/account` Chromium'da görüntülendi; eski URL redirect testleri. PlatformAdmin sidebar ve hesap sayfası da Chromium’da doğrulandı (`screens/ux-05-after-platform-account.webp`). |
| UX-06 | Kullanıcı kontrolünde | Gerçek demo PostgreSQL listener keşfi, credential doğrulaması, `demo_orders` seçimi → wizard özetindeki agent/instance/veritabanı → UI üzerinden kaydetme HTTP 201. Zamanlamasız test işi çalıştırılmadı. |
| UX-07 | Kullanıcı kontrolünde | Hazır/doğrulanıyor/uygun değil gibi durumlar; frontend testleri ve Chromium listesi. |
| UX-08 | Kullanıcı kontrolünde | Agent, tür, isim, snapshot dosya kapsamı, zaman ve boyut listeye yansıyor; 375px Chromium'da belge yatay taşması yok. |
| UX-09 | Kullanıcı kontrolünde | Chromium tenant sidebar'ında tek Yedeklemeler ve dört içerik sekmesi; route/aktif durum testleri. |

**Çalıştırılan testler:** `PasskeyIntegrationTests`: 21 geçti; frontend tam Vitest: 123 dosya, 1274 test geçti (test onarım ajanı); sonradan eklenen kapsam formatörü için artifact/i18n testleri: 89 geçti. `tsc -b`, `oxlint`, `bun run build`, backend API Release build (`-warnaserror`) başarılı. Build büyük JS chunk uyarısı veriyor.

**Görüntüler:** `screens/` altındaki `ux-03-04-before-drawer.webp`, `ux-03-04-after-dialog.webp`, `ux-03-04-after-mobile.webp`, `ux-05-before-topbar.webp`, `ux-05-after-account.webp`, `ux-05-09-after-sidebar.webp`, `ux-06-before-wizard.webp`, `ux-06-after-prefill.webp`, `ux-07-08-before-artifacts.webp`, `ux-07-08-after-artifacts.webp`, `ux-07-08-after-mobile.webp`, `ux-07-08-after-en.webp`, `ux-09-before-sidebar.webp`. Orijinal passkey 500 için ekran görüntüsü alınmamış; sunucu logu saklı kanıttır.

**Açık doğrulamalar:** fiziksel authenticator, gerçek kullanıcı iptali ve ekran okuyucu test edilmedi. Yanlış origin ve RP ID integration testlerinde reddedildi; gerçek tarayıcıda denenmedi. Chromium’da uygulamanın kendi görünüm kontrolleriyle `dark hc` sınıfları etkinleştirildi; masaüstü ve 375px görüntüler alındı, mobil belge taşması görülmedi (`screens/ux-07-08-after-dark-high-contrast*.webp`). Otomatik kontrast ölçümü yapılmadı. Paylaşım listesi dosya adını hâlâ `artifactName` üzerinden gösterir. Mailpit dışına mail gönderilmez. Demo agent imajında PostgreSQL istemci araçları olmadığından yeni keşif işinin gerçek backup çalıştırması denenmedi.

## UX-10 — Yedekleme sağlığı etkenlerinin açıklamaları

| Ekran | Problem | Beklenen davranış | Öncelik | Durum |
|---|---|---|---|---|
| Yedekleme işi düzenleme → Yedekleme sağlığı | Etken açıklamaları sürekli görünerek kartı kalabalıklaştırıyor. | Açıklama ilgili etken üzerinde fareyle, klavye odağıyla veya dokunmayla görüntülensin; puanlar görünür kalsın. | P2 | Kullanıcı kontrolünde |

Önce: `screens/health-hover-before.webp`. Sonra: `screens/health-hover-after.webp`, `screens/health-hover-after-mobile.webp`. Backend ve puan hesaplaması değişmedi.

## UX-11 — Yedekleme işi düzenleme sayfası

| Ekran | Problem | Beklenen davranış | Öncelik | Durum |
|---|---|---|---|---|
| Yedekleme işi düzenleme | Tüm form alanları ve açıklamalar aynı anda görünerek sayfayı uzatıyor. | İlgili ayarlar gruplar halinde isteğe bağlı açılsın; kaydetme ve hataların bulunabilirliği korunsun. | P2 | Kullanıcı kontrolünde |

Kaynak, Depolama, Zamanlama ve Koruma grupları kapalı başlar; geçersiz alan içeren gruplar kaydetme denemesinde açılır. Önce: `screens/edit-page-before.webp`; sonra: `screens/edit-page-after.webp`, `screens/edit-page-after-mobile.webp`. Chromium'da Depolama grubunun açıldığı ve 375 px'te sayfanın yatay taşmadığı görüldü. İlgili 87 test, typecheck ve lint başarılı. Mevcut işe değişiklik kaydedilmedi.

**Sonraki kullanıcı geri bildirimi — sekmeler:** Akordeon yerine Kaynak (varsayılan), Depolama, Zamanlama ve Koruma sekmeleri kullanıldı. İş adı, bilgisayar ve kaynak seçimi ilk sekmede; dosya filtreleri ayrı açılır bölümde. Sekmeler arası form durumu ve ortak Kaydet/Vazgeç korunur; hatalı alanın sekmesi açılır. Önce: `screens/edit-page-after.webp`; sonra: `screens/edit-tabs-final.webp`, `screens/edit-tabs-storage.webp`, `screens/edit-tabs-mobile.webp`. Chromium'da dört sekme ve 375 px görünümü kontrol edildi; ilgili 85 test ve typecheck geçti. Kullanıcı onayı bekleniyor.

## UX-12–14 — İş durumu ve depolama sadeliği

| ID | Ekran | Problem | Beklenen davranış | Öncelik | Durum |
|---|---|---|---|---|---|
| UX-12 | İş düzenleme | Teknik ayrıntılar yineleniyor, işin etkinleştirme eylemi kolay bulunmuyor. | İş kimliği ayrı tanımlansın; iş durumu ve değiştirme eylemi başlıkta görülsün. | P2 | Kullanıcı kontrolünde |
| UX-13 | Depolama seçimi | Çalışma alanı yönetimli etiketi gereksiz tekrar. | Tenant yönetimli hedeflerde bu ifade gösterilmesin; platform yönetimi ayrımı korunsun. | P2 | Kullanıcı kontrolünde |
| UX-14 | Depolama | Çoğaltma ve teknik alanlar ilk bakışta fazla ayrıntı sunuyor. | Çoğaltma isteğe bağlı açılsın; önek ve geçici alan varsayılan görünümde yer almasın. | P2 | Kullanıcı kontrolünde |

İş durumu değişikliği hemen uygulanır; form kaydı etkinlik durumunu yeniden göndermez ve bekleyen form değişikliklerini korur. Önek ve geçici alan değerleri korunur ve yalnızca teknik ayrıntılardan düzenlenebilir. Çoğaltma konfigürasyonu korunur, doğrulama hatasında bölümü açılır. Önce: `screens/edit-tabs-final.webp`, `screens/edit-tabs-storage.webp`; sonra: `screens/editor-status-after.webp`, `screens/storage-simplified-after.webp`, `screens/editor-status-after-mobile.webp`. Chromium'da kullanıcı verisi değiştirilmeden görünüm kontrol edildi; 123 dosyada 1288 frontend testi ve typecheck geçti. İşin gerçek etkinlik durumu tarayıcıda değiştirilmedi.
