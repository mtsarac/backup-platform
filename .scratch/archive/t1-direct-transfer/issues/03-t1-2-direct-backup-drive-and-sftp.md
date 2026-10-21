# 03: T1.2 Doğrudan Backup (Google Drive + SFTP Upload ve ReadBack)

**What to build:** Agent'ın veritabanı (MSSQL / PostgreSQL) yedeğini alıp doğrudan Google Drive veya SFTP hedefine yüklemesi (upload), ardından bağımsız hash ile readback doğrulamasını yapması; backend üzerinden sıfır artifact payload baytı geçmesi.

**Blocked by:** 02: T1.1 Ortak Agent Execution, Yetkilendirme ve Relay Reddi

**Status:** done

- [x] Agent üzerinde Google Drive resumable upload ve SFTP streaming upload akışının kurulması
- [x] E1 (bpe v2) artifact şifrelemesinin doğrudan upload akışında Agent üzerinde korunması
- [ ] Faz 2A bant genişliği sınırlamasının (agent/tenant/job tavanları) AgentDirect upload sırasında korunması
- [ ] Upload tamamlandıktan sonra zorunlu bağımsız ReadBack doğrulaması ve checksum karşılaştırması
- [ ] Backend kontrol düzlemine yalnızca metadata ve sonuç hash'inin iletilmesi; backend HTTP upload endpoint'ine dosya baytı gitmediğinin kanıtlanması
