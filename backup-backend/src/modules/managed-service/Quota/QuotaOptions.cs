namespace BackupPlatform.ManagedService.Quota;

/// <summary>Operational settings of the quota layer (configuration section <c>ManagedService:Quota</c>). Policy values are data, not options.</summary>
public sealed class QuotaOptions
{
    /// <summary>How long an admitted run may hold a reservation. Reservations are released when the run ends; this only bounds a run that never does. A run whose agent still holds a live lease keeps its reservation past this limit; it lapses once the lease does.</summary>
    public TimeSpan ReservationTimeToLive { get; set; } = TimeSpan.FromHours(6);
    public bool MaintenanceEnabled { get; set; } = true;
    /// <summary>How often the maintenance loop expires reservations, initializes missing projections and reconciles the oldest ones.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>A projection is reconciled against its sources when it was last computed longer ago than this.</summary>
    public TimeSpan ReconcileAfter { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Tenants handled per maintenance tick.</summary>
    public int MaintenanceBatchSize { get; set; } = 50;

    public void ThrowIfInvalid()
    {
        if (ReservationTimeToLive < TimeSpan.FromMinutes(1) || ReservationTimeToLive > TimeSpan.FromDays(7))
        {
            throw new InvalidOperationException("ManagedService:Quota:ReservationTimeToLive must be between 1 minute and 7 days.");
        }
        if (MaintenanceInterval < TimeSpan.FromSeconds(1) || MaintenanceInterval > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("ManagedService:Quota:MaintenanceInterval must be between 1 second and 1 day.");
        }
        if (ReconcileAfter < TimeSpan.Zero || ReconcileAfter > TimeSpan.FromDays(30))
        {
            throw new InvalidOperationException("ManagedService:Quota:ReconcileAfter must be between 0 and 30 days.");
        }
        if (MaintenanceBatchSize is < 1 or > 1000)
        {
            throw new InvalidOperationException("ManagedService:Quota:MaintenanceBatchSize must be between 1 and 1000.");
        }
    }
}
