Backup Platform Backend @VERSION@ - Linux x64
=============================================

Self-contained ASP.NET Core build: no .NET runtime has to be installed. It does NOT serve the web UI; deploy the
backup-frontend archive behind a reverse proxy on the same origin (the UI calls /api).

Contents
  bin/                      published API host (BackupPlatform.Api) and its runtime
  sql/migrations.sql        idempotent PostgreSQL schema script for this version (EF Core migrations)
  config/backend.env.example  every backend setting with explanations (no values)

Deploy
  1. Create a PostgreSQL database and a login that owns it.
  2. Apply the schema:  psql "<connection>" -f sql/migrations.sql   (safe to re-run; the backend never migrates by itself)
  3. Provide the connection string through the environment, never a file in this archive:
       BACKUP_PLATFORM_CONNECTION_STRING='Host=...;Database=...;Username=...;Password=...'
  4. Create the first administrator once: ./bin/BackupPlatform.Api --bootstrap-user
       (reads BACKUP_PLATFORM_BOOTSTRAP_LOGIN / _PASSWORD / _DISPLAY_NAME; see config/backend.env.example)
  5. Run ./bin/BackupPlatform.Api with standard ASP.NET Core settings (ASPNETCORE_URLS, HTTPS certificate).
     Persist and protect ASP.NET Data Protection keys in production.

The binaries are not code-signed. Verify the download with the SHA256SUMS.txt file attached to the release.
