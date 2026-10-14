namespace BackupPlatform.Notifications;

/// <summary>
/// Backend-wide notification policy (configuration section <c>Notifications</c>): how often conditions are looked for, how deliveries are retried
/// and how much can be sent at once. These are operator limits; a tenant's own policy (event types, thresholds, per-hour cap) lives in the database
/// and can only be stricter than <see cref="MaxNotificationsPerHourCeiling"/>.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>Master switch of both background workers. Off, the API and its views still work; nothing is detected or sent.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often the detector looks at each notification-enabled tenant.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the delivery worker looks for due deliveries; deliveries are durable, this only bounds how late one starts.</summary>
    public TimeSpan DeliveryPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Deliveries claimed per poll.</summary>
    public int DeliveryBatchSize { get; init; } = 20;

    /// <summary>Sends running at once in one process (also the ceiling of simultaneous provider connections).</summary>
    public int MaxConcurrentDeliveries { get; init; } = 4;

    /// <summary>A claimed delivery that is not finished by then is taken over by another worker (crash recovery).</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Total attempts (the first send included) before a delivery is Failed.</summary>
    public int MaxAttempts { get; init; } = 6;

    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Per-send deadline (connect, TLS, protocol exchange). Must stay far below <see cref="LeaseDuration"/>.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A condition that flaps inside this window is not announced again (it is, once the window passes and it still holds).</summary>
    public TimeSpan FlapCooldown { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Events and deliveries older than this are deleted by the maintenance pass.</summary>
    public int HistoryRetentionDays { get; init; } = 90;

    public int MaxChannelsPerTenant { get; init; } = 20;

    /// <summary>Test notifications one channel accepts per hour.</summary>
    public int TestNotificationsPerChannelPerHour { get; init; } = 5;

    /// <summary>Upper bound a tenant's own per-hour cap is clamped to.</summary>
    public int MaxNotificationsPerHourCeiling { get; init; } = 200;

    /// <summary>
    /// CIDR ranges outbound notification traffic (webhooks, the SMTP relay, the SMS gateway) may reach although they are private, loopback or
    /// link-local. Empty by default: only public addresses are reachable.
    /// </summary>
    public string[] AllowedPrivateNetworks { get; init; } = [];

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SweepInterval < TimeSpan.FromSeconds(5)) problems.Add("SweepInterval must be at least 5 seconds.");
        if (DeliveryPollInterval < TimeSpan.FromSeconds(1)) problems.Add("DeliveryPollInterval must be at least one second.");
        if (DeliveryBatchSize is < 1 or > 500) problems.Add("DeliveryBatchSize must be between 1 and 500.");
        if (MaxConcurrentDeliveries is < 1 or > 64) problems.Add("MaxConcurrentDeliveries must be between 1 and 64.");
        if (MaxAttempts is < 1 or > 20) problems.Add("MaxAttempts must be between 1 and 20.");
        if (BackoffBase < TimeSpan.FromSeconds(1) || BackoffMax < BackoffBase) problems.Add("BackoffBase must be at least one second and not exceed BackoffMax.");
        if (SendTimeout < TimeSpan.FromSeconds(1) || LeaseDuration < SendTimeout * 3) problems.Add("LeaseDuration must be at least three times SendTimeout.");
        if (FlapCooldown < TimeSpan.Zero) problems.Add("FlapCooldown must not be negative.");
        if (HistoryRetentionDays < 1) problems.Add("HistoryRetentionDays must be at least 1.");
        if (MaxChannelsPerTenant < 1) problems.Add("MaxChannelsPerTenant must be at least 1.");
        if (TestNotificationsPerChannelPerHour < 1) problems.Add("TestNotificationsPerChannelPerHour must be at least 1.");
        if (MaxNotificationsPerHourCeiling < 1) problems.Add("MaxNotificationsPerHourCeiling must be at least 1.");
        foreach (var network in AllowedPrivateNetworks)
            if (!System.Net.IPNetwork.TryParse(network, out _)) problems.Add($"AllowedPrivateNetworks entry '{network}' is not a CIDR range.");
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0) throw new InvalidOperationException("Invalid notification configuration: " + string.Join(" ", problems));
    }
}
