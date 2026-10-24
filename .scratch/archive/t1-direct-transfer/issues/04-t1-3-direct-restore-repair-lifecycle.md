# 04: T1.3 Doğrudan Restore, Agent Repair ve Yaşam Döngüsü

**What to build:** Doğrudan depolamadan (Drive/SFTP) Agent üzerine restore (precheck, R1 izole doğrulama), harici hedefler arası Agent üzerinden doğrudan replika onarımı (repair) ve depolamadan fiziksel silme/yokluk doğrulama (retention/cleanup).

**Blocked by:** 03: T1.2 Doğrudan Backup (Google Drive + SFTP Upload ve ReadBack)

**Status:** code-complete (live acceptance NOT RUN, see review-followups ticket 13)

- [x] Hedef Agent'ın harici depolamadan (Drive/SFTP) doğrudan okuyarak restore yapması (MSSQL NewDatabase/Replace ve PostgreSQL NewDatabase)
- [x] R1 izole restore doğrulaması senaryolarının doğrudan depolama okumasıyla çalışması
- [x] Harici→harici replica repair işleminin yetkili bir Agent üzerinde, ciphertext bozulmadan ve backend'e payload uğramadan yürütülmesi
- [x] Retention ve orphan cleanup işlemlerinde Agent'ın provider üzerinde silme yapıp yokluğu (absence) doğrulaması; backend'in atomik audit ve kota düşümü yapması
