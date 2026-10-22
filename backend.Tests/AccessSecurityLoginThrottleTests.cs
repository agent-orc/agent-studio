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
        var overflowClients = OverflowClients(100);
        for (var i = 0; i < overflowClients.Length; i++)
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("invented." + (i + 4), "wrong", overflowClients[i])).Status);

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
            Assert.Equal(i < 5 ? 401 : 429, Assert.Throws<SecurityOperationException>(() =>
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
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", "192.0.2.2")).Status);
        Assert.Equal((1, 1, 1, 0), store.LoginStateCounts);
    }

    [Fact]
    public void Unknown_and_known_usernames_fail_alike_at_failure_table_capacity()
    {
        var clients = OverflowClients(3);
        var hashes = 0;
        string? workFactor = null;
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:MaxTrackedClients"] = "1",
            ["Security:LoginThrottle:MaxTrackedUsernames"] = "1",
            ["Security:LoginThrottle:ClientMaxFailures"] = "2",
            ["Security:LoginThrottle:ClientLockoutMinutes"] = "5",
            ["Security:LoginThrottle:UsernameMaxFailures"] = "100"
        }, (password, hash) =>
        {
            hashes++;
            workFactor = string.Join('$', hash.Split('$').Take(2));
            return password == Password;
        });
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented", "wrong", "192.0.2.1")).Status);

        (int Status, string Code, string Message, int Hashes, string? WorkFactor) Fail(string username, string client)
        {
            var before = hashes;
            workFactor = null;
            var error = Assert.Throws<SecurityOperationException>(() => store.Login(username, "wrong", client));
            return (error.Status, error.Code, error.Message, hashes - before, workFactor);
        }

        // Both exact tables are full. Each path checks one password at the same cost.
        var invalid = (401, "invalid-credentials", "Username or password is incorrect.", 1,
            "pbkdf2-sha512$600000");
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(invalid, Fail("not.an.account", clients[0]));
            Assert.Equal(invalid, Fail("owner", clients[1]));
        }

        // Both overflow client budgets are spent, so neither path hashes again.
        var throttled = (429, "login-throttled", "Too many failed attempts. Try again shortly.", 0, (string?)null);
        Assert.Equal(throttled, Fail("not.an.account", clients[0]));
        Assert.Equal(throttled, Fail("owner", clients[1]));
        Assert.Equal((1, 1, 0, 0), store.LoginStateCounts);

        Assert.Equal("owner", store.Login("owner", Password, clients[2]).User.Username);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(invalid, Fail("not.an.account", clients[0]));
        Assert.Equal(invalid, Fail("owner", clients[1]));
        Assert.Equal(throttled, Fail("not.an.account", clients[0]));
        Assert.Equal(throttled, Fail("owner", clients[1]));
    }

    [Fact]
    public void Full_failure_tables_count_wrong_passwords_and_bound_hash_work()
    {
        var hashes = 0;
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:MaxTrackedClients"] = "1",
            ["Security:LoginThrottle:MaxTrackedUsernames"] = "1",
            ["Security:LoginThrottle:ClientMaxFailures"] = "100",
            ["Security:LoginThrottle:UsernameMaxFailures"] = "2",
            ["Security:LoginThrottle:UsernameDelaySeconds"] = "3"
        }, (password, _) =>
        {
            hashes++;
            return password == Password;
        });
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented", "wrong", "192.0.2.1")).Status);

        for (var i = 0; i < 2; i++)
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("owner", "wrong", "192.0.2.2")).Status);
        var afterBudget = hashes;
        for (var i = 0; i < 20; i++)
            Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
                store.Login("owner", "wrong", "192.0.2.2")).Status);
        Assert.Equal(afterBudget, hashes);
        Assert.Equal((1, 1, 0, 0), store.LoginStateCounts);

        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("owner", store.Login("owner", Password, "192.0.2.2").User.Username);
        Assert.Equal(afterBudget + 1, hashes);
    }

    [Fact]
    public void Overflow_client_lockout_outlasts_short_delay_and_failure_window()
    {
        const string overflowClient = "192.0.2.2";
        var newClient = Enumerable.Range(3, 200)
            .Select(i => "198.51.100." + i)
            .First(client => LoginOverflowBudget.BucketIndex(client)
                != LoginOverflowBudget.BucketIndex(overflowClient));
        var hashes = 0;
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:MaxTrackedClients"] = "1",
            ["Security:LoginThrottle:MaxTrackedUsernames"] = "1",
            ["Security:LoginThrottle:ClientMaxFailures"] = "2",
            ["Security:LoginThrottle:ClientLockoutMinutes"] = "3",
            ["Security:LoginThrottle:UsernameMaxFailures"] = "100",
            ["Security:LoginThrottle:WindowMinutes"] = "1",
            ["Security:LoginThrottle:UsernameDelaySeconds"] = "2"
        }, (password, _) => { hashes++; return password == Password; });
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented", "wrong", "192.0.2.1")).Status);

        for (var i = 0; i < 2; i++)
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("owner", "wrong", overflowClient)).Status);
        var afterBudget = hashes;
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", Password, overflowClient)).Status);
        Assert.Equal(afterBudget, hashes);

        Assert.Equal("owner", store.Login("owner", Password, newClient).User.Username);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", Password, overflowClient)).Status);
        Assert.Equal(afterBudget + 1, hashes);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("owner", store.Login("owner", Password, overflowClient).User.Username);
        Assert.Equal(afterBudget + 2, hashes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Active_overflow_budget_survives_exact_table_capacity_opening(bool clientBudget)
    {
        var hashes = 0;
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:WindowMinutes"] = "1",
            ["Security:LoginThrottle:MaxTrackedClients"] = clientBudget ? "1" : "10",
            ["Security:LoginThrottle:MaxTrackedUsernames"] = clientBudget ? "10" : "1",
            ["Security:LoginThrottle:ClientMaxFailures"] = clientBudget ? "2" : "100",
            ["Security:LoginThrottle:UsernameMaxFailures"] = clientBudget ? "100" : "2",
            ["Security:LoginThrottle:UsernameDelaySeconds"] = "3"
        }, (_, _) => { hashes++; return false; });
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented", "wrong", "192.0.2.1")).Status);

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", "192.0.2.2")).Status);
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", "192.0.2.2")).Status);
        Assert.Equal(3, hashes);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", "192.0.2.2")).Status);
        Assert.Equal(3, hashes);
    }

    [Fact]
    public void Full_delay_table_still_allows_correct_password_after_short_wait()
    {
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:UsernameMaxFailures"] = "1",
            ["Security:LoginThrottle:ClientMaxFailures"] = "100",
            ["Security:LoginThrottle:UsernameDelaySeconds"] = "3",
            ["Security:LoginThrottle:MaxDelayEntries"] = "1"
        }, (password, _) => password == Password);
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", "192.0.2.1")).Status);
        Assert.Equal(1, store.LoginStateCounts.Delays);

        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", Password, "192.0.2.2")).Status);
        Assert.Equal(1, store.LoginStateCounts.Delays);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("owner", store.Login("owner", Password, "192.0.2.2").User.Username);
        Assert.Equal(1, store.LoginStateCounts.Delays);
    }

    [Fact]
    public void Full_username_budget_gives_each_client_its_own_retry_wait()
    {
        const string attacker = "192.0.2.2";
        var newcomer = Enumerable.Range(3, 200)
            .Select(i => "198.51.100." + i)
            .First(client => LoginOverflowBudget.BucketIndex("owner|" + client)
                != LoginOverflowBudget.BucketIndex("owner|" + attacker));
        var store = NewStore(new()
        {
            ["Security:LoginThrottle:MaxTrackedClients"] = "1",
            ["Security:LoginThrottle:MaxTrackedUsernames"] = "1",
            ["Security:LoginThrottle:ClientMaxFailures"] = "100",
            ["Security:LoginThrottle:UsernameMaxFailures"] = "1",
            ["Security:LoginThrottle:UsernameDelaySeconds"] = "3"
        }, (password, _) => password == Password);
        store.Bootstrap(new BootstrapRequest("owner", Password, null));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented", "wrong", "192.0.2.1")).Status);
        store.Login("owner", Password, "192.0.2.5");
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", attacker)).Status);

        Assert.Equal("owner", store.Login("owner", Password, "192.0.2.5").User.Username);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", Password, newcomer)).Status);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("owner", "wrong", attacker)).Status);
        Assert.Equal("owner", store.Login("owner", Password, newcomer).User.Username);
    }

    [Fact]
    public void Overflow_budget_expires_failures_and_enforces_retry()
    {
        var budget = new LoginOverflowBudget();
        var window = TimeSpan.FromMinutes(1);
        var delay = TimeSpan.FromSeconds(3);
        var now = _clock.GetUtcNow().UtcDateTime;
        budget.RecordFailure("client", "client", now, window, 2, delay);
        budget.RecordFailure("client", "client", now, window, 2, delay);
        Assert.False(budget.CanHash("client", "client", now, window, 2, delay));

        _clock.Advance(delay);
        now = _clock.GetUtcNow().UtcDateTime;
        Assert.True(budget.CanHash("client", "client", now, window, 2, delay));
        budget.RecordFailure("client", "client", now, window, 2, delay);
        Assert.False(budget.CanHash("client", "client", now, window, 2, delay));

        _clock.Advance(TimeSpan.FromMinutes(2));
        now = _clock.GetUtcNow().UtcDateTime;
        Assert.True(budget.CanHash("client", "client", now, window, 2, delay));
        budget.RecordFailure("client", "client", now, window, 2, delay);
        Assert.True(budget.CanHash("client", "client", now, window, 2, delay));
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
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented.3", "wrong", "192.0.2.3")).Status);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented.3", "wrong", "192.0.2.3")).Status);
        Assert.Equal((2, 1, 0, 0), store.LoginStateCounts);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("invented.3", "wrong", "192.0.2.3")).Status);
        Assert.Equal((0, 0, 0, 0), store.LoginStateCounts);

        _clock.Advance(TimeSpan.FromMinutes(1));
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

    private static string[] OverflowClients(int count)
    {
        var buckets = new HashSet<int>();
        return Enumerable.Range(0, 65536).Select(i => $"198.51.{i / 256}.{i % 256}")
            .Where(client => buckets.Add(LoginOverflowBudget.BucketIndex(client)))
            .Take(count).ToArray();
    }

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
