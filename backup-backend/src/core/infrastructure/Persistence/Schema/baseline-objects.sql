-- Objects the EF model cannot express: seed rows, then the immutability/guard functions and triggers (dumped from the final pre-baseline schema).
INSERT INTO quota_policies ("Id", "Key", "Name", "NormalizedName", "Version", "AccountingMode", "IncludedStorageScopes", "EnforcementMode",
                            "WarningThresholdPercent", "CriticalThresholdPercent", "Enabled", "IsDefault", "CreatedAt", "UpdatedAt")
VALUES ('14e8fce9-11ed-47a9-aa04-b6ead7a8cd3d', 'default', 'Default (logical, all storage)', 'default (logical, all storage)', 1, 1,
        '[{"kind":"all"}]'::jsonb, 1, 80, 90, TRUE, TRUE, now(), now());

INSERT INTO plans ("Id", "Name", "NormalizedName", "Enabled", "StorageLimitBytes", "QuotaPolicyReference", "PolicyConfigurationVersion", "IsSystem", "SystemKey", "CreatedAt", "UpdatedAt")
VALUES ('61ff8c7d-5cf1-5b91-8be8-a9e25d5f53ea', '1 GB', '1 gb', TRUE, 1000000000, NULL, 1, FALSE, NULL, now(), now()),
       ('a6a9a7d1-293a-5ec9-a30f-603428c86559', '5 GB', '5 gb', TRUE, 5000000000, NULL, 1, FALSE, NULL, now(), now()),
       ('16aeeb9a-192d-518f-861c-fd3e417ca8ec', '10 GB', '10 gb', TRUE, 10000000000, NULL, 1, FALSE, NULL, now(), now()),
       ('bd58c99e-3efa-5c02-920c-b54e1230f3e6', '20 GB', '20 gb', TRUE, 20000000000, NULL, 1, FALSE, NULL, now(), now()),
       ('f31c79cf-c393-53c2-969d-90bcf0f63ef7', 'Legacy Unlimited', 'legacy unlimited', TRUE, NULL, NULL, 1, TRUE, 'legacy_unlimited', now(), now());

CREATE FUNCTION public.backup_attempt_terminal_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD."Status" IN (4,5,6,7) AND (NEW."Status" IS DISTINCT FROM OLD."Status"
        OR NEW."TerminalReason" IS DISTINCT FROM OLD."TerminalReason"
        OR NEW."CompletedAt" IS DISTINCT FROM OLD."CompletedAt") THEN
        RAISE EXCEPTION 'A terminal backup attempt is immutable.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.backup_occurrence_immutable() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'A decided schedule occurrence is immutable.' USING ERRCODE = '23514';
END $$;


CREATE FUNCTION public.backup_replica_pin_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM restore_replica_pins WHERE "ReplicaId" = NEW."Id" AND "ReleasedAt" IS NULL)
       OR EXISTS (SELECT 1 FROM download_sessions WHERE "ReplicaId" = NEW."Id" AND "TenantId" = NEW."TenantId"
           AND "ReleasedAt" IS NULL AND "ExpiresAt" > clock_timestamp()
           AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" > clock_timestamp()))
       OR EXISTS (SELECT 1 FROM replica_repair_operations WHERE "SourceReplicaId" = NEW."Id" AND "TenantId" = NEW."TenantId"
           AND "Status" IN ('Pending', 'InProgress')) THEN
        RAISE EXCEPTION 'A pinned replica cannot begin its physical deletion.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;


CREATE FUNCTION public.backup_run_terminal_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."Status" IS DISTINCT FROM OLD."Status" AND (OLD."Status" IN (4,6) OR (OLD."Status" IN (5,7) AND NEW."Status" <> 1)) THEN
        RAISE EXCEPTION 'A terminal backup run cannot change state.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.download_lifecycle_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD."ReleasedAt" IS NOT NULL OR NEW."ArtifactId" <> OLD."ArtifactId"
       OR NEW."TenantId" <> OLD."TenantId" OR NEW."UserId" <> OLD."UserId" OR NEW."ExpiresAt" < OLD."ExpiresAt"
       OR NEW."SupportSessionId" IS DISTINCT FROM OLD."SupportSessionId"
       OR NEW."Checksum" IS DISTINCT FROM OLD."Checksum" OR NEW."SizeBytes" IS DISTINCT FROM OLD."SizeBytes"
       OR ((OLD."ExpiresAt" <= clock_timestamp() OR OLD."LeaseExpiresAt" <= clock_timestamp()) AND NEW."ReleasedAt" IS NULL) THEN
        RAISE EXCEPTION 'A released or expired download cannot reacquire its hold.' USING ERRCODE = '23514';
    END IF;
    IF NEW."ReplicaId" IS DISTINCT FROM OLD."ReplicaId" AND
       (OLD."State" NOT IN ('Ready', 'Streaming') OR NEW."State" IS DISTINCT FROM OLD."State"
        OR OLD."TransferredBytes" <> 0 OR NEW."TransferredBytes" <> 0 OR NEW."ReleasedAt" IS NOT NULL
        OR NEW."ExpiresAt" IS DISTINCT FROM OLD."ExpiresAt" OR NEW."Filename" IS DISTINCT FROM OLD."Filename"
        OR NEW."CreatedAt" IS DISTINCT FROM OLD."CreatedAt"
        OR NEW."LeaseOwner" IS DISTINCT FROM OLD."LeaseOwner" OR NEW."LeaseExpiresAt" IS DISTINCT FROM OLD."LeaseExpiresAt") THEN
        RAISE EXCEPTION 'A download source can change only before transfer under its live lease.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.download_replica_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    replica backup_replicas%ROWTYPE;
    artifact backup_artifacts%ROWTYPE;
BEGIN
    SELECT * INTO artifact FROM backup_artifacts WHERE "Id" = NEW."ArtifactId" AND "TenantId" = NEW."TenantId" FOR NO KEY UPDATE;
    SELECT * INTO replica FROM backup_replicas WHERE "Id" = NEW."ReplicaId" AND "TenantId" = NEW."TenantId" AND "ArtifactId" = NEW."ArtifactId" FOR UPDATE;
    IF replica."Id" IS NULL OR artifact."Id" IS NULL OR replica."Status" <> 'Available' OR replica."VerificationState" <> 'Verified'
       OR replica."Checksum" IS DISTINCT FROM artifact."ArtifactChecksum" OR replica."SizeBytes" IS DISTINCT FROM artifact."SizeBytes"
       OR artifact."ChecksumAlgorithm" <> 'sha256' OR NEW."Checksum" IS DISTINCT FROM artifact."ArtifactChecksum"
       OR NEW."SizeBytes" IS DISTINCT FROM artifact."SizeBytes"
       OR NEW."StorageProvider" IS DISTINCT FROM replica."StorageProvider" OR NEW."StorageTarget" IS DISTINCT FROM replica."StorageTarget"
       OR NEW."Key" IS DISTINCT FROM replica."Key"
       OR NEW."AgentId" IS DISTINCT FROM (CASE WHEN replica."ExecutionLocation" = 'Agent' THEN replica."OwningAgentId"
                                               WHEN TG_OP = 'INSERT' AND replica."StorageProvider" IN ('sftp', 'google-drive') THEN NEW."AgentId"
                                               ELSE NULL END)
       OR (replica."ExecutionLocation" <> 'Agent' AND TG_OP = 'INSERT' AND replica."StorageProvider" IN ('sftp', 'google-drive') AND NEW."AgentId" IS NULL)
       OR (TG_OP = 'UPDATE' AND replica."ExecutionLocation" <> 'Backend') THEN
        RAISE EXCEPTION 'An ineligible replica cannot be downloaded.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.restore_attempt_terminal_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD."Status" IN ('Succeeded','Failed','Cancelled','AgentLost') AND (NEW."Status" IS DISTINCT FROM OLD."Status"
        OR NEW."FailureCode" IS DISTINCT FROM OLD."FailureCode"
        OR NEW."TargetState" IS DISTINCT FROM OLD."TargetState"
        OR NEW."CompletedAt" IS DISTINCT FROM OLD."CompletedAt") THEN
        RAISE EXCEPTION 'A terminal restore attempt is immutable.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.restore_fallback_operation_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD."Status" <> 'Pending' OR OLD."StartedAt" IS NOT NULL OR OLD."CancellationRequestedAt" IS NOT NULL
       OR NEW."Status" NOT IN ('Pending', 'Leased') OR NEW."StartedAt" IS NOT NULL OR NEW."CancellationRequestedAt" IS NOT NULL THEN
        RAISE EXCEPTION 'A restore source cannot change after handoff.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.restore_fallback_pin_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    operation restore_operations%ROWTYPE;
    replica backup_replicas%ROWTYPE;
BEGIN
    IF OLD."ReleasedAt" IS NOT NULL OR NEW."ReleasedAt" IS NOT NULL
       OR NEW."OperationId" IS DISTINCT FROM OLD."OperationId" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId" THEN
        RAISE EXCEPTION 'A released restore pin cannot be moved.' USING ERRCODE = '23514';
    END IF;
    SELECT * INTO operation FROM restore_operations WHERE "Id" = NEW."OperationId" AND "TenantId" = NEW."TenantId";
    SELECT * INTO replica FROM backup_replicas WHERE "Id" = NEW."ReplicaId" AND "TenantId" = NEW."TenantId"
        AND "ArtifactId" = operation."ArtifactId" FOR UPDATE;
    IF operation."Id" IS NULL OR operation."Status" NOT IN ('Pending', 'Leased') OR operation."StartedAt" IS NOT NULL
       OR operation."CancellationRequestedAt" IS NOT NULL OR replica."Id" IS NULL
       OR replica."Status" NOT IN ('Available', 'DeletePending') OR replica."VerificationState" <> 'Verified' THEN
        RAISE EXCEPTION 'An ineligible restore source cannot be pinned.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.restore_operation_terminal_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD."Status" IN ('Succeeded','Failed','Cancelled','AgentLost') AND (NEW."Status" IS DISTINCT FROM OLD."Status"
        OR NEW."FailureCode" IS DISTINCT FROM OLD."FailureCode"
        OR NEW."TargetState" IS DISTINCT FROM OLD."TargetState"
        OR NEW."CompletedAt" IS DISTINCT FROM OLD."CompletedAt"
        OR NEW."ResultJson" IS DISTINCT FROM OLD."ResultJson") THEN
        RAISE EXCEPTION 'A terminal restore is immutable.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.restore_pin_release_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD."ReleasedAt" IS NOT NULL AND NEW."ReleasedAt" IS DISTINCT FROM OLD."ReleasedAt" THEN
        RAISE EXCEPTION 'A released replica pin cannot change.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.restore_pin_replica_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    replica_status text;
BEGIN
    IF NEW."ReleasedAt" IS NULL THEN
        SELECT "Status" INTO replica_status FROM backup_replicas
            WHERE "Id" = NEW."ReplicaId" AND "TenantId" = NEW."TenantId" FOR UPDATE;
        IF replica_status IN ('Deleting','DeleteFailed','Deleted') THEN
            RAISE EXCEPTION 'A replica that is being or has been deleted cannot be pinned.' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.support_access_session_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'A support access session is never deleted.' USING ERRCODE = '23514';
    END IF;
    IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
        OR NEW."PlatformAdminId" IS DISTINCT FROM OLD."PlatformAdminId" OR NEW."Permissions" IS DISTINCT FROM OLD."Permissions"
        OR NEW."Reason" IS DISTINCT FROM OLD."Reason" OR NEW."StartedAt" IS DISTINCT FROM OLD."StartedAt"
        OR NEW."ExpiresAt" IS DISTINCT FROM OLD."ExpiresAt" OR NEW."CreatedAt" IS DISTINCT FROM OLD."CreatedAt"
        OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey" OR NEW."RequestFingerprint" IS DISTINCT FROM OLD."RequestFingerprint" THEN
        RAISE EXCEPTION 'The tenant, administrator, permissions, reason and window of a support session are immutable.' USING ERRCODE = '23514';
    END IF;
    IF OLD."Status" <> 'Active' THEN
        RAISE EXCEPTION 'An ended support session is final.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


CREATE FUNCTION public.support_session_link_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."SupportSessionId" IS DISTINCT FROM OLD."SupportSessionId" THEN
        RAISE EXCEPTION 'The support session of an operation cannot change.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


SET default_tablespace = '';

SET default_table_access_method = heap;

CREATE TRIGGER trg_backup_attempt_terminal_guard BEFORE UPDATE ON public.backup_run_attempts FOR EACH ROW EXECUTE FUNCTION public.backup_attempt_terminal_guard();


CREATE TRIGGER trg_backup_occurrence_immutable BEFORE UPDATE ON public.backup_schedule_occurrences FOR EACH ROW EXECUTE FUNCTION public.backup_occurrence_immutable();


CREATE TRIGGER trg_backup_replica_pin_guard BEFORE UPDATE OF "Status" ON public.backup_replicas FOR EACH ROW WHEN ((((new."Status")::text = ANY ((ARRAY['Deleting'::character varying, 'DeleteFailed'::character varying, 'Deleted'::character varying])::text[])) AND ((new."Status")::text IS DISTINCT FROM (old."Status")::text))) EXECUTE FUNCTION public.backup_replica_pin_guard();


CREATE TRIGGER trg_backup_run_terminal_guard BEFORE UPDATE ON public.backup_runs FOR EACH ROW EXECUTE FUNCTION public.backup_run_terminal_guard();


CREATE TRIGGER trg_download_fallback_replica_guard BEFORE UPDATE OF "ReplicaId" ON public.download_sessions FOR EACH ROW WHEN ((new."ReplicaId" IS DISTINCT FROM old."ReplicaId")) EXECUTE FUNCTION public.download_replica_guard();


CREATE TRIGGER trg_download_lifecycle_guard BEFORE UPDATE ON public.download_sessions FOR EACH ROW EXECUTE FUNCTION public.download_lifecycle_guard();


CREATE TRIGGER trg_download_replica_guard BEFORE INSERT ON public.download_sessions FOR EACH ROW EXECUTE FUNCTION public.download_replica_guard();


CREATE TRIGGER trg_download_sessions_support_link BEFORE UPDATE ON public.download_sessions FOR EACH ROW EXECUTE FUNCTION public.support_session_link_guard();


CREATE TRIGGER trg_restore_attempt_terminal_guard BEFORE UPDATE ON public.restore_attempts FOR EACH ROW EXECUTE FUNCTION public.restore_attempt_terminal_guard();


CREATE TRIGGER trg_restore_fallback_operation_guard BEFORE UPDATE OF "ReplicaId" ON public.restore_operations FOR EACH ROW WHEN ((new."ReplicaId" IS DISTINCT FROM old."ReplicaId")) EXECUTE FUNCTION public.restore_fallback_operation_guard();


CREATE TRIGGER trg_restore_fallback_replica_guard BEFORE UPDATE OF "ReplicaId" ON public.restore_replica_pins FOR EACH ROW WHEN ((new."ReplicaId" IS DISTINCT FROM old."ReplicaId")) EXECUTE FUNCTION public.restore_fallback_pin_guard();


CREATE TRIGGER trg_restore_operation_terminal_guard BEFORE UPDATE ON public.restore_operations FOR EACH ROW EXECUTE FUNCTION public.restore_operation_terminal_guard();


CREATE TRIGGER trg_restore_operations_support_link BEFORE UPDATE ON public.restore_operations FOR EACH ROW EXECUTE FUNCTION public.support_session_link_guard();


CREATE TRIGGER trg_restore_pin_release_guard BEFORE UPDATE ON public.restore_replica_pins FOR EACH ROW EXECUTE FUNCTION public.restore_pin_release_guard();


CREATE TRIGGER trg_restore_pin_replica_guard BEFORE INSERT ON public.restore_replica_pins FOR EACH ROW EXECUTE FUNCTION public.restore_pin_replica_guard();


CREATE TRIGGER trg_support_access_session_guard BEFORE DELETE OR UPDATE ON public.support_access_sessions FOR EACH ROW EXECUTE FUNCTION public.support_access_session_guard();
