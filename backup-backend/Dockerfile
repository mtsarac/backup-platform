FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
RUN apt-get update && apt-get install -y --no-install-recommends zip && rm -rf /var/lib/apt/lists/*
WORKDIR /src

COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY dotnet-tools.json .

COPY src/core/contracts/BackupPlatform.Contracts.csproj src/core/contracts/
COPY src/core/domain/BackupPlatform.Domain.csproj src/core/domain/
COPY src/core/application/BackupPlatform.Application.csproj src/core/application/
COPY src/core/infrastructure/BackupPlatform.Infrastructure.csproj src/core/infrastructure/
COPY src/modules/identity/BackupPlatform.Identity.csproj src/modules/identity/
COPY src/modules/tenancy/BackupPlatform.Tenancy.csproj src/modules/tenancy/
COPY src/modules/agents/BackupPlatform.Agents.csproj src/modules/agents/
COPY src/modules/managed-service/BackupPlatform.ManagedService.csproj src/modules/managed-service/
COPY src/modules/storage/BackupPlatform.Storage.csproj src/modules/storage/
COPY src/modules/notifications/BackupPlatform.Notifications.csproj src/modules/notifications/
COPY src/providers/secret/abstractions/BackupPlatform.SecretProviders.Abstractions.csproj src/providers/secret/abstractions/
COPY src/providers/secret/data-protection/BackupPlatform.SecretProviders.DataProtection.csproj src/providers/secret/data-protection/
COPY src/providers/storage/abstractions/BackupPlatform.StorageProviders.Abstractions.csproj src/providers/storage/abstractions/
COPY src/providers/storage/local/BackupPlatform.StorageProviders.Local.csproj src/providers/storage/local/
COPY src/providers/storage/central/BackupPlatform.StorageProviders.Central.csproj src/providers/storage/central/
COPY src/providers/storage/sftp/BackupPlatform.StorageProviders.Sftp.csproj src/providers/storage/sftp/
COPY src/providers/storage/google-drive/BackupPlatform.StorageProviders.GoogleDrive.csproj src/providers/storage/google-drive/
COPY src/hosts/api/BackupPlatform.Api.csproj src/hosts/api/

RUN dotnet restore src/hosts/api/BackupPlatform.Api.csproj

COPY deploy/ deploy/
COPY src/ src/

RUN dotnet tool restore
RUN dotnet publish src/hosts/api/BackupPlatform.Api.csproj -c Release -o /app/publish

ENV BACKUP_PLATFORM_CONNECTION_STRING="Host=dummy"
RUN dotnet ef migrations bundle --project src/core/infrastructure --startup-project src/core/infrastructure -o /app/publish/efbundle --no-build --configuration Release

# Build agent release packages (Windows zip & Linux tar.gz)
RUN mkdir -p /app/publish/agent-dist && \
    mkdir -p /tmp/win-stage/backup-agent-win-x64-1.0.0/bin /tmp/win-stage/backup-agent-win-x64-1.0.0/config && \
    dotnet publish src/hosts/agent/BackupPlatform.Agent.Host.csproj -c Release -r win-x64 --self-contained true -p:AgentPlatform=Windows -p:Version=1.0.0 -o /tmp/win-stage/backup-agent-win-x64-1.0.0/bin && \
    cp deploy/windows/Install-BackupAgentService.ps1 deploy/windows/Uninstall-BackupAgentService.ps1 /tmp/win-stage/backup-agent-win-x64-1.0.0/ && \
    cp deploy/release/agent.settings.example.json /tmp/win-stage/backup-agent-win-x64-1.0.0/config/ && \
    sed "s/@VERSION@/1.0.0/g" deploy/release/README-agent-windows.txt > /tmp/win-stage/backup-agent-win-x64-1.0.0/README.txt && \
    (cd /tmp/win-stage && zip -qr /app/publish/agent-dist/backup-agent-win-x64.zip backup-agent-win-x64-1.0.0) && \
    rm -rf /tmp/win-stage && \
    mkdir -p /tmp/linux-stage/backup-agent-linux-x64-1.0.0/bin /tmp/linux-stage/backup-agent-linux-x64-1.0.0/config && \
    dotnet publish src/hosts/agent/BackupPlatform.Agent.Host.csproj -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -p:Version=1.0.0 -o /tmp/linux-stage/backup-agent-linux-x64-1.0.0/bin && \
    cp deploy/release/agent.settings.example.json /tmp/linux-stage/backup-agent-linux-x64-1.0.0/config/ && \
    sed "s/@VERSION@/1.0.0/g" deploy/release/README-agent-linux.txt > /tmp/linux-stage/backup-agent-linux-x64-1.0.0/README.txt && \
    tar -C /tmp/linux-stage -czf /app/publish/agent-dist/backup-agent-linux-x64.tar.gz backup-agent-linux-x64-1.0.0 && \
    rm -rf /tmp/linux-stage

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY docker-entrypoint.sh /app/
RUN chmod +x /app/docker-entrypoint.sh && mkdir -p /app/dp-keys

ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    Secrets__DataProtection__KeysDirectory=/app/dp-keys \
    AgentDistribution__PackagesDirectory=/app/agent-dist

EXPOSE 5000
ENTRYPOINT ["/app/docker-entrypoint.sh"]
