using System.Text.Json;

namespace Restaurant.Api;

// Test/development fixture only. Program explicitly forbids it in Production.
public sealed class InMemoryGroupRepository : IGroupRepository
{
    private readonly object gate = new();
    private readonly Dictionary<string, GroupAggregate> groups = new(StringComparer.Ordinal);
    private static GroupAggregate Copy(GroupAggregate group)
        => JsonSerializer.Deserialize<GroupAggregate>(JsonSerializer.Serialize(group))!;

    public InMemoryGroupRepository(IEnumerable<GroupAggregate> initialGroups)
    {
        foreach (var group in initialGroups) groups.Add(group.Id, Copy(group));
    }

    public Task<GroupAggregate?> GetGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(groups.TryGetValue(groupId, out var group) ? Copy(group) : null);
    }

    private Task<string?> FindAsync(Func<GroupAggregate, bool> predicate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(groups.Values.FirstOrDefault(predicate)?.Id);
    }
    public Task<string?> FindGroupByReservationAsync(string reservationId, CancellationToken cancellationToken)
        => FindAsync(group => group.ReservationId == reservationId, cancellationToken);
    public Task<string?> FindGroupBySessionAsync(string sessionId, CancellationToken cancellationToken)
        => FindAsync(group => group.Participants.Any(person => person.Id == sessionId), cancellationToken);
    public Task<string?> FindGroupByInviteAsync(string token, CancellationToken cancellationToken)
        => FindAsync(group => group.InviteToken == token, cancellationToken);

    public Task<bool> SaveAsync(GroupAggregate group, long expectedVersion, string? removedSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!groups.TryGetValue(group.Id, out var current) || current.Version != expectedVersion)
                return Task.FromResult(false);
            if (removedSessionId is not null && !current.Participants.Any(person => person.Id == removedSessionId))
                return Task.FromResult(false);
            groups[group.Id] = Copy(group);
            return Task.FromResult(true);
        }
    }
}

public static class DevelopmentFixture
{
    // OwnerSubject is an example OIDC sub, never a credential or authentication bypass.
    public static GroupAggregate Create(string ownerSubject = "workshop-owner-sub")
    {
        var salad = new Dish("dish-salad", "Spring Salad", 12m);
        var bowl = new Dish("dish-bowl", "Avocado Pine Nut Bowl", 14.50m);
        return new GroupAggregate(
            "workshop-group", "workshop-reservation", ownerSubject, "workshop-owner-session",
            "Open", "workshop-order", "Draft", "workshop-public-invite",
            new GuestInvite("workshop-group", "workshop-location", "Green & Tasty", "Rustaveli Avenue 48",
                "2026-10-28T16:00:00Z", "2026-10-28T17:30:00Z", 5, 7, "Owner"),
            [
                new("workshop-owner-session", "Owner", "Owner", true, [new(salad, 1), new(bowl, 1)]),
                new("workshop-guest-session", "Guest", "Guest", true, [new(salad, 2)]),
                new("workshop-pending-session", "Guest", "Still choosing", false, [new(bowl, 1)])
            ], []);
    }
}
