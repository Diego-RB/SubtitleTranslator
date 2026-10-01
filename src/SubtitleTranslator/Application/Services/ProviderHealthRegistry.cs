using System.Collections.Concurrent;

namespace SubtitleTranslator.Application.Services;

public enum ProviderHealthState
{
    Unknown,
    Online,
    Offline,
    Degraded
}

public sealed record ProviderHealthSnapshot(
    int ProviderId,
    ProviderHealthState State,
    string Message,
    DateTimeOffset? CheckedAt);

public sealed class ProviderHealthRegistry
{
    private readonly ConcurrentDictionary<int, ProviderHealthSnapshot> snapshots = new();

    public ProviderHealthSnapshot Get(int providerId) => snapshots.TryGetValue(providerId, out var snapshot)
        ? snapshot
        : new ProviderHealthSnapshot(providerId, ProviderHealthState.Unknown, "Health check has not run yet.", null);

    public void Update(ProviderHealthSnapshot snapshot) => snapshots[snapshot.ProviderId] = snapshot;
}
