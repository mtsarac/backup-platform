# 0002: Tenant members are administrators

A UserAdmin has full administrative rights in every tenant they belong to, including destructive backup, restore, share and agent operations. We retain this single tenant authorization level rather than introduce operator or read-only viewer roles; tenant membership must therefore be granted only to users trusted with all of those actions. PlatformAdmin remains a separate platform role. This decision addresses the authorization finding in [#258](https://github.com/mtsarac/backup-platform/issues/258).
