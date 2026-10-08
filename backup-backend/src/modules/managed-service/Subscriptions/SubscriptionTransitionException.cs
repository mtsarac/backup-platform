namespace BackupPlatform.ManagedService.Subscriptions;

public static class SubscriptionTransitionCodes
{
    public const string NotActive = "subscription_not_active";
    public const string NotSuspended = "subscription_not_suspended";
    public const string Expired = "subscription_expired";
    public const string NotOpen = "subscription_not_open";
}

/// <summary>A state change that the subscription's current state does not allow. The code is safe to return to clients.</summary>
public sealed class SubscriptionTransitionException(string code) : InvalidOperationException($"Subscription transition refused ({code}).")
{
    public string Code { get; } = code;
}
