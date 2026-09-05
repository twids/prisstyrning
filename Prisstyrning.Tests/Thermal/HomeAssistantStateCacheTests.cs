using System.Text.Json.Nodes;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantStateCacheTests
{
    private static readonly DateTimeOffset Revision = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Session_RequiresConfirmedSubscriptionAndCompleteSnapshot()
    {
        var cache = new HomeAssistantStateCache();
        var session = cache.BeginSession("account-a", Revision)!;

        Assert.Equal(HomeAssistantLivePhase.Connecting, cache.ReadAccount("account-a").Phase);
        Assert.False(cache.PublishSnapshot(session, [State("sensor.room", "21", Revision)]));
        Assert.False(cache.IsConnected("account-a"));
        Assert.True(cache.BeginSnapshot(session));
        Assert.True(cache.ApplyEvent(session, Change("sensor.room", "22", Revision.AddMinutes(1))));
        Assert.Empty(cache.Snapshot("account-a"));
        Assert.True(cache.PublishSnapshot(session, [State("sensor.room", "21", Revision)]));
        Assert.True(cache.IsConnected("account-a"));
        Assert.Equal("22", Assert.Single(cache.Snapshot("account-a")).State);
        Assert.False(cache.PublishSnapshot(session, [State("sensor.room", "19", Revision)]));
    }

    [Fact]
    public void Invalidate_ImmediatelyClearsOnlyChangedAccountAndRejectsEveryOldCallback()
    {
        var cache = new HomeAssistantStateCache();
        var old = Connected(cache, "account-a", Revision, "21");
        Connected(cache, "account-b", Revision, "20");
        var changedRevision = Revision.AddMinutes(1);
        cache.Invalidate("account-a", changedRevision, telemetryEnabled: true);

        var changed = cache.ReadAccount("account-a");
        Assert.Equal(HomeAssistantLivePhase.Reloading, changed.Phase);
        Assert.Empty(changed.States);
        Assert.Null(changed.LastSnapshotUtc);
        Assert.Null(changed.LastActivityUtc);
        Assert.Null(cache.BeginSession("account-a", Revision));
        Assert.False(cache.ApplyEvent(old, Change("sensor.room", "99", changedRevision)));
        Assert.False(cache.PublishSnapshot(old, [State("sensor.room", "99", changedRevision)]));
        cache.EndSession(old);
        Assert.Equal(HomeAssistantLivePhase.Reloading, cache.ReadAccount("account-a").Phase);
        Assert.True(cache.IsConnected("account-b"));
        Assert.Equal("20", Assert.Single(cache.Snapshot("account-b")).State);
    }

    [Fact]
    public void LateDisconnectAndPoll_CannotRetireNewerConnection()
    {
        var cache = new HomeAssistantStateCache();
        var old = Connected(cache, "account-a", Revision, "21");
        var current = Connected(cache, "account-a", Revision.AddMinutes(1), "22");
        cache.EndSession(old);
        cache.RetireRevision("account-a", Revision);
        cache.Invalidate("account-a", Revision, telemetryEnabled: false);

        Assert.True(cache.IsConnected("account-a"));
        Assert.Equal(current.ConfigurationUpdatedAtUtc, cache.ReadAccount("account-a").ConfigurationUpdatedAtUtc);
        Assert.Equal("22", Assert.Single(cache.Snapshot("account-a")).State);
    }

    [Fact]
    public void Disable_RetiresRevisionUntilANewerConfigurationIsSaved()
    {
        var cache = new HomeAssistantStateCache();
        var old = Connected(cache, "account-a", Revision, "21");
        cache.Invalidate("account-a", Revision, telemetryEnabled: false);
        Assert.Null(cache.BeginSession("account-a", Revision));
        Assert.False(cache.ApplyEvent(old, Change("sensor.room", "99", Revision.AddMinutes(1))));
        Assert.Empty(cache.Snapshot("account-a"));
        Assert.Equal(HomeAssistantLivePhase.Disabled, cache.ReadAccount("account-a").Phase);
        Assert.NotNull(cache.BeginSession("account-a", Revision.AddTicks(10)));
    }

    [Fact]
    public void Disconnect_RejectsLateEventsAndReconnectRequiresNewSnapshot()
    {
        var cache = new HomeAssistantStateCache();
        var old = Connected(cache, "account-a", Revision, "21");
        cache.EndSession(old);
        Assert.False(cache.IsConnected("account-a"));
        Assert.Equal(HomeAssistantLivePhase.Reconnecting, cache.ReadAccount("account-a").Phase);
        Assert.False(cache.ApplyEvent(old, Change("sensor.room", "99", Revision)));
        Assert.Single(cache.Snapshot("account-a")); // explicitly unverified diagnostic data

        var retry = cache.BeginSession("account-a", Revision)!;
        Assert.Empty(cache.Snapshot("account-a"));
        Assert.True(cache.BeginSnapshot(retry));
        Assert.True(cache.PublishSnapshot(retry, [State("sensor.other", "20", Revision)]));
        Assert.Equal("sensor.other", Assert.Single(cache.Snapshot("account-a")).EntityId);
        cache.EndSession(old);
        Assert.True(cache.IsConnected("account-a"));
    }

    [Theory]
    [InlineData(-1, "21")]
    [InlineData(0, "22")]
    [InlineData(1, "22")]
    public void Snapshot_MergesBufferedEventsByHaTime(int eventMinutes, string expected)
    {
        var cache = new HomeAssistantStateCache();
        var session = cache.BeginSession("account-a", Revision)!;
        cache.BeginSnapshot(session);
        cache.ApplyEvent(session, Change("sensor.room", "22", Revision.AddMinutes(eventMinutes)));
        cache.PublishSnapshot(session, [State("sensor.room", "21", Revision)]);

        Assert.Equal(expected, Assert.Single(cache.Snapshot("account-a")).State);
    }

    [Fact]
    public void RemovalDuringSnapshot_IsNotUndoneByAnOlderLiveEvent()
    {
        var cache = new HomeAssistantStateCache();
        var session = cache.BeginSession("account-a", Revision)!;
        cache.BeginSnapshot(session);
        cache.ApplyEvent(session, new("sensor.room", null, Revision.AddMinutes(1)));
        cache.PublishSnapshot(session, [State("sensor.room", "21", Revision)]);
        Assert.Empty(cache.Snapshot("account-a"));

        cache.ApplyEvent(session, Change("sensor.room", "21", Revision));
        Assert.Empty(cache.Snapshot("account-a"));
        cache.ApplyEvent(session, Change("sensor.room", "22", Revision.AddMinutes(2)));
        Assert.Equal("22", Assert.Single(cache.Snapshot("account-a")).State);
    }

    [Fact]
    public void OlderRemoval_DoesNotDeleteNewerSnapshotValue()
    {
        var cache = new HomeAssistantStateCache();
        var session = cache.BeginSession("account-a", Revision)!;
        cache.BeginSnapshot(session);
        cache.ApplyEvent(session, new("sensor.room", null, Revision));
        cache.PublishSnapshot(session, [State("sensor.room", "22", Revision.AddMinutes(1))]);
        Assert.Equal("22", Assert.Single(cache.Snapshot("account-a")).State);
    }

    [Fact]
    public async Task Snapshot_IsNotVisiblePartwayThroughEnumeration()
    {
        var cache = new HomeAssistantStateCache();
        var session = cache.BeginSession("account-a", Revision)!;
        cache.BeginSnapshot(session);
        using var halfway = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        IEnumerable<HomeAssistantState> States()
        {
            yield return State("sensor.first", "21", Revision);
            halfway.Set();
            if (!finish.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test enumeration was not released.");
            yield return State("sensor.second", "22", Revision);
        }

        var publish = Task.Run(() => cache.PublishSnapshot(session, States()));
        try
        {
            Assert.True(halfway.Wait(TimeSpan.FromSeconds(5)));
            var during = cache.ReadAccount("account-a");
            Assert.Empty(during.States);
            Assert.False(during.Connected);
        }
        finally { finish.Set(); }
        Assert.True(await publish.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, cache.Snapshot("account-a").Count);
    }

    [Fact]
    public void RetireRevision_DoesNotPermitAnOldPollToRestartDeletedConnection()
    {
        var cache = new HomeAssistantStateCache();
        Connected(cache, "account-a", Revision, "21");
        cache.RetireRevision("account-a", Revision);
        Assert.Empty(cache.Snapshot("account-a"));
        Assert.Null(cache.BeginSession("account-a", Revision));
    }

    private static HomeAssistantCacheSession Connected(HomeAssistantStateCache cache, string account, DateTimeOffset revision, string value)
    {
        var session = cache.BeginSession(account, revision)!;
        cache.BeginSnapshot(session);
        cache.PublishSnapshot(session, [State("sensor.room", value, revision)]);
        return session;
    }

    [Fact]
    public void Refresh_UpdatesUnchangedReportWithoutReplacingNewerEventsOrResurrectingRemoval()
    {
        var cache = new HomeAssistantStateCache();
        var session = Connected(cache, "account-a", Revision, "21");
        var version = cache.BeginRefresh(session)!.Value;
        var report = State("sensor.room", "21", Revision) with { LastReportedUtc = Revision.AddMinutes(5) };
        Assert.True(cache.PublishRefresh(session, version, [report]));
        Assert.Equal(report.LastReportedUtc, Assert.Single(cache.Snapshot("account-a")).LastReportedUtc);

        version = cache.BeginRefresh(session)!.Value;
        cache.ApplyEvent(session, Change("sensor.room", "22", Revision.AddMinutes(6)));
        cache.PublishRefresh(session, version, [report]);
        Assert.Equal("22", Assert.Single(cache.Snapshot("account-a")).State);

        version = cache.BeginRefresh(session)!.Value;
        cache.ApplyEvent(session, new("sensor.room", null, Revision.AddMinutes(7)));
        cache.PublishRefresh(session, version, [report]);
        Assert.Empty(cache.Snapshot("account-a"));
        cache.PublishRefresh(session, cache.BeginRefresh(session)!.Value, [report]);
        Assert.Empty(cache.Snapshot("account-a"));
    }

    [Fact]
    public void Refresh_OldEventCannotReplaceNewerReport_AndMissingEntityIsRemoved()
    {
        var cache = new HomeAssistantStateCache();
        var session = Connected(cache, "account-a", Revision, "21");
        cache.ApplyEvent(session, Change("sensor.room", "21", Revision));
        var report = State("sensor.room", "21", Revision) with { LastReportedUtc = Revision.AddMinutes(5) };
        cache.PublishRefresh(session, cache.BeginRefresh(session)!.Value, [report]);
        cache.ApplyEvent(session, Change("sensor.room", "21", Revision));
        Assert.Equal(report.LastReportedUtc, Assert.Single(cache.Snapshot("account-a")).LastReportedUtc);
        cache.PublishRefresh(session, cache.BeginRefresh(session)!.Value, []);
        Assert.Empty(cache.Snapshot("account-a"));
    }

    [Fact]
    public void Refresh_RevisionChangeAndDisconnectRejectLateRestResults()
    {
        var cache = new HomeAssistantStateCache();
        var session = Connected(cache, "account-a", Revision, "21");
        var version = cache.BeginRefresh(session)!.Value;
        cache.EndSession(session);
        Assert.Null(cache.BeginRefresh(session));
        Assert.False(cache.PublishRefresh(session, version, [State("sensor.room", "99", Revision)]));
        var newer = Connected(cache, "account-a", Revision.AddMinutes(1), "22");
        Assert.False(cache.PublishRefresh(session, version, []));
        Assert.Equal("22", Assert.Single(cache.Snapshot("account-a")).State);
        cache.Invalidate("account-a", newer.ConfigurationUpdatedAtUtc.AddMinutes(1), true);
        Assert.False(cache.PublishRefresh(newer, 0, [State("sensor.room", "99", Revision)]));
        Assert.Empty(cache.Snapshot("account-a"));
    }

    private static HomeAssistantState State(string id, string value, DateTimeOffset updated) =>
        new(id, value, new JsonObject { ["unit_of_measurement"] = "°C" }, updated, updated, DateTimeOffset.UtcNow);

    private static HomeAssistantStateChange Change(string id, string value, DateTimeOffset updated) => new(id, State(id, value, updated), updated);
}
