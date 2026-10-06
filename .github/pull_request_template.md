## What and why

<!-- One or two sentences. Link the issue if there is one. -->

## Checklist

- [ ] **Build and tests**: `dotnet build BackupPlatform.sln -c Release -p:ContinuousIntegrationBuild=true -warnaserror` and the affected test projects pass; frontend changes pass `bun run typecheck`, `lint`, `test` and `build`
- [ ] **Migrations**: no schema change, or the EF migration is included, reviewed and applies on an existing database
- [ ] **Security and secrets**: no credentials, connection strings, tokens or `docs/` content in the diff; new endpoints keep tenant isolation and authorization
- [ ] **Manual verification**: what you ran against a real backend/Agent/database, or why it was not needed
