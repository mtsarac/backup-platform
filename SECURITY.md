# Security model

## Roles

There are two account roles and no per-tenant roles.

- **PlatformAdmin** operates the platform (tenants, accounts, plans, storage, notification transports, audit retention). It has no tenant membership.
- **UserAdmin** administers every tenant it is a member of. An account that loses its last membership is disabled and its sessions end.

Authorization is decided in the backend by the central authorization service; the frontend only mirrors it. The last active PlatformAdmin cannot be disabled.

## Reading backup content: BackupShare

A PlatformAdmin can download a tenant's artifact only through a **BackupShare**: a time-limited, revocable grant for one artifact, created with a reason by a UserAdmin of the tenant or by a PlatformAdmin.

- The share is checked when the download session is created and again when the stream starts. A stream that has started is not cut by expiry.
- Revoking a share cancels downloads in progress on every API node.
- Creating, extending and revoking a share is audited and notified to the tenant's UserAdmins and the PlatformAdmins (except the actor).
- Every download attempt, including refused and failed ones, appears in the share history. History and audit records are kept for the audit retention period (365 days by default, set by a PlatformAdmin).
- A share never hands out an encryption key.

A UserAdmin downloads the artifacts of its own tenants without a share, optionally decrypted.

## Restore boundary

Restore is a tenant management operation and is available to both roles.

- A restore request never accepts a share id and never returns backup bytes or keys to the caller.
- Restoring to a target writes the plaintext to that target. Whoever controls the target can read it, so a restore to a controlled target exposes plaintext.
- This exposure is **not** covered by BackupShare: no share is required, and revoking a share does not stop a restore.
- Every restore is audited with the acting user and the target.

Treat the right to restore as the right to read the data.

## Accounts

- Passwords: at least 12 characters, no composition rule, checked against a bundled list of common and breached passwords. An initial or temporary password must be changed at the next sign-in; until then the session can only change the password or sign out.
- Second factors: TOTP and passkeys, with recovery codes. A session waiting for its second factor cannot reach any other API.
- Sign-in attempts are rate limited per account and per IP address with a counter shared by all API nodes.
- Email verification is optional. Self-service password reset works only for a verified address and only when the platform SMTP relay and `Notifications:PublicBaseUrl` are configured; otherwise a PlatformAdmin assigns a temporary password. Reset and verification tokens are single-use, short-lived and stored hashed, and the reset request answers identically for known and unknown accounts.
- Security events on an account (password or email changes, temporary password, disable and enable, second factor and recovery code changes, suspicious sign-in) always create an in-app notification for the affected user, and an email when the address is verified. This category cannot be switched off.

## Audit

Sensitive operations write an audit event with the acting user. Passwords, keys, tokens and backup plaintext are never written to audit details or logs. Audit events can be sealed with an ECDSA signature (`Audit:SigningKeyId`); the signed payload keeps the historical field name `SupportSessionId` so that existing seals stay verifiable.
