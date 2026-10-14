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

Notification emails (confirmation and unsubscribe links)
  Set Notifications__PublicBaseUrl to the public https origin users reach the API at; without it no email is sent (every mail
  carries an unsubscribe link). The links under /api/notifications/recipient/ are anonymous and token-based:
    - The link a person opens keeps its token in the URL FRAGMENT (#token=...). Browsers never send fragments, so that token
      is in no proxy, CDN or API access log.
    - The one-click unsubscribe (RFC 8058 List-Unsubscribe header) must carry its token in the QUERY of
      POST /api/notifications/recipient/unsubscribe?token=... . It can only stop mail (never start it), but keep it out of logs:
  The API host never logs it (Microsoft.AspNetCore.Hosting.Diagnostics is pinned to Warning in code; do not raise
  Microsoft.AspNetCore request logging for production). Configure your reverse proxy / ingress too:
    Traefik v3 (static config):
      accessLog:
        format: json
        fields:
          queryParameters:
            defaultMode: drop        # drops every query parameter value from the access log
      # Traefik versions without fields.queryParameters: drop the whole path field instead
      #   accessLog.fields.names.RequestPath: drop      (the common format cannot hide the query; use format: json)
      # or switch access logs off for just this route (router label):
      #   traefik.http.routers.<router>.observability.accesslogs=false   with a router rule PathPrefix(`/api/notifications/recipient`)
    nginx:  log $uri (no query) instead of $request / $request_uri for this location, or use access_log off; in that location.
    Other layers (CDN, WAF, APM, browser-side error reporting) must also be configured not to store query strings for this path.
  Rate limits (Notifications__RecipientLinkWindow / RecipientLinkAttemptsPerToken / RecipientLinkInvalidPerClient): each token may be
  used 10 times per minute; a client address may send 20 invalid requests per minute before it receives 429. Valid tokens are never
  limited by address, so a mail provider's unsubscribe cannot be starved. Behind a reverse proxy the client address is the proxy's
  unless you configure forwarded headers: the invalid-request budget is then shared, which is harmless for genuine requests.

The binaries are not code-signed. Verify the download with the SHA256SUMS.txt file attached to the release.
