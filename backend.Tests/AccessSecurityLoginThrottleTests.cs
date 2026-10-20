using System.Net;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AccessSecurityLoginThrottleTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-login-throttle-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(DateTimeOffset.Parse("2026-10-20T08:00:00Z"));

    [Fact]
    public void Many_usernames_from_one_client_exhaust_the_client_budget()
    {
        var store = NewStore(new() { ["Security:LoginThrottle:ClientMaxFailures"] = "3" });
        store.Bootstrap(new BootstrapRequest("owner", Password, null));

        for (var i = 0; i < 3; i++)
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("made.up." + i, "wrong password", "192.0.2.10")).Status);

        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", Password, "192.0.2.10")).Status);
        Assert.Equal("owner", store.Login("owner", Password, "192.0.2.11").User.Username);
        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("owner", store.Login("owner", Password, "192.0.2.10").User.Username);
    }

    [Fact]
    public void Oversized_usernames_count_only_against_the_client_budget()
    {
        var store = NewStore(new() { ["Security:LoginThrottle:ClientMaxFailures"] = "2" }, (_, _) => false);
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        var oversized = new string('x', 100_000);

        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login(oversized, "wrong password", "192.0.2.1")).Status);
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login(oversized + "2", "wrong password", "192.0.2.1")).Status);
        Assert.Equal((1, 0, 0, 0), store.LoginStateCounts);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", Password, "192.0.2.1")).Status);
    }

    [Fact]
    public void Distinct_unknown_users_and_clients_cannot_evict_active_failure_budgets()
    {
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:MaxTrackedClients"] = "4",
            ["Security:LoginThrottle:MaxTrackedUsernames"] = "4",
            ["Security:LoginThrottle:ClientMaxFailures"] = "2",
            ["Security:LoginThrottle:UsernameMaxFailures"] = "100"
        }, (_, _) => false);
        store.Bootstrap(new BootstrapRequest("owner", Password, null));

        for (var i = 0; i < 4; i++)
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("invented." + i, "wrong", "192.0.2." + i)).Status);
        for (var i = 4; i < 104; i++)
            Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
                store.Login("invented." + i, "wrong", "198.51.100." + i)).Status);

        Assert.Equal((4, 4, 0, 0), store.LoginStateCounts);
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented.0", "wrong", "192.0.2.0")).Status);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented.0", "wrong", "192.0.2.0")).Status);
        Assert.Equal((4, 4, 0, 0), store.LoginStateCounts);
    }

    [Fact]
    public void Many_unknown_usernames_from_one_client_keep_the_username_table_bounded()
    {
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:MaxTrackedUsernames"] = "3",
            ["Security:LoginThrottle:ClientMaxFailures"] = "5"
        }, (_, _) => false);
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        for (var i = 0; i < 3; i++)
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("invented." + i, "wrong", "192.0.2.1")).Status);
        for (var i = 3; i < 103; i++)
            Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
                store.Login("invented." + i, "wrong", "192.0.2.1")).Status);
        Assert.Equal((1, 3, 0, 0), store.LoginStateCounts);
    }

    [Fact]
    public void Known_account_can_sign_in_when_failure_tables_are_full()
    {
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:MaxTrackedClients"] = "1",
            ["Security:LoginThrottle:MaxTrackedUsernames"] = "1"
        }, (password, _) => password == Password);
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented", "wrong", "192.0.2.1")).Status);

        Assert.Equal("owner", store.Login("owner", Password, "192.0.2.2").User.Username);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", "192.0.2.2")).Status);
        Assert.Equal((1, 1, 1, 0), store.LoginStateCounts);
    }

    [Fact]
    public void Expired_failure_budgets_release_capacity_but_active_lockouts_remain()
    {
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:WindowMinutes"] = "1",
            ["Security:LoginThrottle:ClientLockoutMinutes"] = "3",
            ["Security:LoginThrottle:ClientMaxFailures"] = "1",
            ["Security:LoginThrottle:MaxTrackedClients"] = "2",
            ["Security:LoginThrottle:MaxTrackedUsernames"] = "2"
        }, (_, _) => false);
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        foreach (var i in new[] { 1, 2 })
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("invented." + i, "wrong", "192.0.2." + i)).Status);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented.3", "wrong", "192.0.2.3")).Status);
        Assert.Equal((2, 0, 0, 0), store.LoginStateCounts);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented.3", "wrong", "192.0.2.3")).Status);
        Assert.Equal((1, 1, 0, 0), store.LoginStateCounts);
    }

    [Fact]
    public void Recent_successes_and_delays_expire_and_stay_bounded()
    {
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:WindowMinutes"] = "1",
            ["Security:LoginThrottle:RecentSuccessMinutes"] = "2",
            ["Security:LoginThrottle:UsernameMaxFailures"] = "1",
            ["Security:LoginThrottle:ClientMaxFailures"] = "100",
            ["Security:LoginThrottle:MaxRecentSuccesses"] = "3",
            ["Security:LoginThrottle:MaxDelayEntries"] = "3"
        }, (password, _) => password == Password);
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        for (var i = 0; i < 30; i++)
            store.Login("owner", Password, "192.0.2." + i);
        Assert.Equal(3, store.LoginStateCounts.RecentSuccesses);

        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", "192.0.2.30")).Status);
        for (var i = 31; i < 60; i++)
            Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
                store.Login("owner", "wrong", "192.0.2." + i)).Status);
        Assert.Equal(3, store.LoginStateCounts.Delays);
        _clock.Advance(TimeSpan.FromMinutes(3));
        // Any login triggers expiry without relying on wall-clock time.
        store.Login("owner", Password, "192.0.2.90");
        Assert.Equal((0, 0, 1, 0), store.LoginStateCounts);
    }

    [Fact]
    public void Username_budget_delays_new_clients_but_preserves_a_recently_successful_client()
    {
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:UsernameMaxFailures"] = "2",
            ["Security:LoginThrottle:UsernameDelaySeconds"] = "3"
        });
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        store.Login("owner", Password, "192.0.2.1");
        foreach (var client in new[] { "192.0.2.2", "192.0.2.3" })
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("owner", "wrong password", client)).Status);

        Assert.Equal("owner", store.Login("owner", Password, "192.0.2.1").User.Username);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", Password, "192.0.2.4")).Status);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("owner", store.Login("owner", Password, "192.0.2.4").User.Username);
    }

    [Fact]
    public async Task Blocked_hash_does_not_hold_the_store_lock_or_block_a_different_client()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = NewStore(verifyPassword: (password, hash) =>
        {
            if (password == "flood")
            {
                entered.Set();
                release.Wait();
            }
            return PasswordSecretHasher.VerifyPassword(password, hash);
        });
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        var runner = store.EnrollRunner(store.CreateEnrollment(new RunnerEnrollmentRequest("runner", null, null, null)).Code);
        var flood = Task.Run(() => Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented", "flood", "192.0.2.20")));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var otherWork = Task.Run(() =>
            {
                Assert.NotNull(store.AuthenticateRunner(runner.Secret));
                return store.Login("owner", Password, "192.0.2.21");
            });
            var owner = await otherWork.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(store.AuthenticateSession(owner.SessionToken));
        }
        finally { release.Set(); }
        Assert.Equal(401, (await flood).Status);
    }

    [Fact]
    public async Task Password_change_during_hash_refuses_the_stale_record()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = NewStore(verifyPassword: (password, hash) =>
        {
            if (password == Password)
            {
                entered.Set();
                release.Wait();
            }
            return PasswordSecretHasher.VerifyPassword(password, hash);
        });
        var owner = store.Bootstrap(new BootstrapRequest("owner", Password, null));
        var login = Task.Run(() => Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", Password, "192.0.2.30")));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Task.Run(() => store.ResetPassword(owner.User.Id,
                new PasswordResetRequest("new correct horse battery staple!"))).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); }
        Assert.Equal(401, (await login).Status);
    }

    [Fact]
    public async Task Concurrent_hash_limit_refuses_extra_work()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = NewStore(new() { ["Security:LoginThrottle:MaxConcurrentHashes"] = "1" }, (password, hash) =>
        {
            entered.Set();
            release.Wait();
            return PasswordSecretHasher.VerifyPassword(password, hash);
        });
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        var first = Task.Run(() => Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented", "wrong password", "192.0.2.40")));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
                store.Login("owner", Password, "192.0.2.41")).Status);
        }
        finally { release.Set(); }
        Assert.Equal(401, (await first).Status);
    }

    [Theory]
    [InlineData("192.0.2.4", "192.0.2.4")]
    [InlineData("::ffff:192.0.2.4", "192.0.2.4")]
    [InlineData("2001:db8:1:2::12", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::34", "2001:db8:1:2::/64")]
    public void Client_key_uses_ipv4_address_or_ipv6_64(string address, string expected)
        => Assert.Equal(expected, AccessSecurityEndpoints.ClientThrottleKey(IPAddress.Parse(address)));

    private AccessSecurityStore NewStore(Dictionary<string, string?>? overrides = null,
        Func<string, string, bool>? verifyPassword = null)
    {
        var values = new Dictionary<string, string?> { ["TaskRepository"] = _root };
        if (overrides is not null)
            foreach (var (key, value) in overrides) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance,
            _clock, verifyPassword ?? PasswordSecretHasher.VerifyPassword);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
