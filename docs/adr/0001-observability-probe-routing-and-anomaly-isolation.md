# 0001: Hybrid Capacity Probe Routing and Advisory-Only Anomaly Isolation

## Context and Decision
Storage capacity monitoring (Phase 21) queries physical target availability across diverse network locations. We decided that cloud and platform-managed targets (Google Drive, S3, Central) are queried directly by the Backend, whereas on-premises and private-network targets (Local volume, LAN SFTP) are queried by the authorized Agent via lightweight periodic `CapacityProbe` tasks.

Furthermore, backup health degradation (Phase 22) and artifact size anomalies (Phase 23, evaluated via moving-average Z-scores) are isolated as non-blocking `SizeAnomalyWarning` advisory states. They trigger N1 notifications (`capacity_critical`, `backup_size_anomaly`) and dashboard indicators, but never abort or mark a backup attempt as failed.

## Consequences
- Preserves the T1 zero-payload rule for private storage without forcing unnecessary agent hops for cloud APIs already authenticated on the backend.
- Prevents false-positive size deviations (e.g. valid large data purges or sudden bulk imports) from interrupting scheduled backup execution.
- If no authorized agent is online for a LAN/SFTP target, its physical capacity is reported as unavailable until the next successful probe run.
