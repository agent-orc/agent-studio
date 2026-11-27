using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every generated timestamp and integration observation is fixed.
public sealed class ReadyClaimPropertyTests
{
    private static readonly DateTime Epoch = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private const string Host = "host-a";
    private const int FirstSeed = 406700;
    private const int Examples = 256;

    private sealed record Board(TaskInfo[] Cards, Dictionary<string, TaskIntegrationStatus> Integration,
        Dictionary<string, ProjectSettings> Projects, int Slots, int Occupied);

    [Theory]
    [InlineData("integration")]
    [InlineData("routing")]
    [InlineData("capacity")]
    [InlineData("work-conserving")]
    [InlineData("cycles")]
    [InlineData("lane-order")]
    public void Random_boards_obey_ready_claim_properties(string property)
    {
        for (var seed = FirstSeed; seed < FirstSeed + Examples; seed++)
        {
            var board = Generate(seed);
            if (Violation(property, board) is not { } failure) continue;
            var shrunk = Shrink(board, candidate => Violation(property, candidate) is not null);
            Assert.Fail($"Property {property}, seed {seed}: {failure}\n"
                + $"Shrunk counterexample: {JsonSerializer.Serialize(new
                {
                    shrunk.Slots, shrunk.Occupied,
                    Cards = shrunk.Cards.Select(c => new { c.Key, c.State, c.ProjectName, c.Order,
                        Dependencies = Dependencies(c), Integration = shrunk.Integration.GetValueOrDefault(c.TaskKey) }),
                })}");
        }
    }

    [Fact]
    public void Self_dependency_is_reported_and_never_claimed()
    {
        var card = WithDependencies(RegressionCard(0), "CARD-0");
        var board = RegressionBoard([card]);
        Assert.True(TaskReferenceIndex.Build(board.Cards).EvaluateWaitsOn(card).CycleDetected);
        Assert.Empty(Sweep(board));
    }

    // Seed 406703 shrinks to two Ready cards with mutual edges and one integrated predecessor.
    [Fact]
    public void Integrated_cycle_predecessor_does_not_make_a_cycle_member_claimable()
    {
        var a = WithDependencies(RegressionCard(0), "CARD-1");
        var b = WithDependencies(RegressionCard(1), "CARD-0");
        var board = RegressionBoard([a, b]);
        board.Integration[b.TaskKey] = new() { Status = IntegrationStatuses.Integrated, IntegrationBranch = "develop" };
        Assert.Empty(Sweep(board));
        // With one free slot, the invalid head must not starve a later unrelated card.
        Assert.Equal("CARD-2", Assert.Single(Sweep(board with { Cards = [a, b, RegressionCard(2)] })).Key);
    }

    private static TaskInfo RegressionCard(int number) => new()
    {
        Id = "card-" + number, Key = "CARD-" + number, TaskKey = "task-" + number,
        ProjectName = "project-0", State = TaskStates.Ready, Agent = AgentTypes.Codex,
        Order = number, CreatedAt = Epoch, EnteredLaneAt = Epoch,
    };

    private static Board RegressionBoard(TaskInfo[] cards) => new(cards, [],
        new() { ["project-0"] = new() { PickupMode = PickupModes.Auto, ExecutionLocation = Host } }, 1, 0);

    private static string? Violation(string property, Board board)
    {
        var claims = Sweep(board);
        var index = TaskReferenceIndex.Build(board.Cards);
        var eligible = board.Cards.Where(card => ModelEligible(board, card)).ToArray();
        switch (property)
        {
            case "integration":
                foreach (var card in claims)
                    if (!ModelDependenciesFulfilled(board, card)) return $"Unintegrated predecessor of {card.Key}";
                break;
            case "routing":
                if (claims.Any(card => card.State != TaskStates.Ready
                    || board.Projects[card.ProjectName].ExecutionLocation is not (Host or "class:linux")))
                    return "Claim violated a lane or host pin";
                break;
            case "capacity":
                if (claims.Count > Math.Max(0, board.Slots - board.Occupied)) return "Host slots exceeded";
                if (claims.DistinctBy(card => card.Key).Count() != claims.Count) return "Duplicate claim";
                foreach (var project in board.Projects)
                {
                    if (project.Value.ExecutionLocation != "class:linux") continue;
                    var occupied = board.Cards.Count(c => c.ProjectName == project.Key && c.State == TaskStates.Progress);
                    if (claims.Count(c => c.ProjectName == project.Key) > Math.Max(0, project.Value.MaxParallelism - occupied))
                        return $"Project slots exceeded: {project.Key}";
                }
                break;
            case "work-conserving":
                if (board.Slots > board.Occupied && eligible.Any(card => ModelProjectSlot(board, card)) && claims.Count == 0)
                    return "A claimable card and free slot produced no claim";
                // A sweep must fill every usable slot, including after skipping blocked heads.
                var after = Advance(board, claims);
                if (after.Slots > after.Occupied && after.Cards.Any(card => ModelEligible(after, card) && ModelProjectSlot(after, card)))
                    return "Sweep stopped with usable capacity and eligible work";
                break;
            case "cycles":
                foreach (var card in board.Cards)
                {
                    var expected = OnCycle(board, card.Key!);
                    var status = index.EvaluateWaitsOn(card, board.Integration, "develop");
                    if (status.CycleDetected != expected) return $"Wrong cycle report for {card.Key}";
                    if (!expected) continue;
                    if (claims.Any(c => c.Key == card.Key)) return $"Cycle member claimed: {card.Key}";
                    if (status.CyclePath.Count < 2 || status.CyclePath[0] != status.CyclePath[^1])
                        return "Cycle report is not a closed path";
                    for (var i = 1; i < status.CyclePath.Count; i++)
                        if (!Dependencies(board.Cards.Single(c => c.Key == status.CyclePath[i - 1])).Contains(status.CyclePath[i]))
                            return "Cycle report contains a nonexistent edge";
                }
                if (board.Slots > board.Occupied && eligible.Any(card => ModelProjectSlot(board, card)) && claims.Count == 0)
                    return "Cycles starved unrelated work";
                break;
            case "lane-order":
                foreach (var project in board.Projects.Keys)
                {
                    var expected = ModelOrder(eligible.Where(c => c.ProjectName == project)).Select(c => c.Key).ToArray();
                    var actual = claims.Where(c => c.ProjectName == project).Select(c => c.Key).ToArray();
                    if (!actual.SequenceEqual(expected.Take(actual.Length))) return $"Lane order violated in {project}";
                }
                break;
        }
        return null;
    }

    [Fact]
    public void Same_seed_regenerates_identical_boards_claims_and_cycle_reports()
    {
        for (var seed = FirstSeed; seed < FirstSeed + Examples; seed++)
        {
            var first = Generate(seed);
            var second = Generate(seed);
            Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
            Assert.Equal(Fingerprint(first), Fingerprint(second));
        }
    }

    private static string[] Fingerprint(Board board)
    {
        var index = TaskReferenceIndex.Build(board.Cards);
        return Sweep(board).Select(c => "claim:" + c.Key).Concat(board.Cards.Select(c =>
            JsonSerializer.Serialize(index.EvaluateWaitsOn(c, board.Integration, "develop")))).ToArray();
    }

    // Policy-level claim harness: the same eligibility, ordering and capacity calls as LeaseEndpoints.
    // Repositories, credentials, quotas and capabilities are assumed available. No HTTP host or CLI is started.
    private static List<TaskInfo> Sweep(Board board)
    {
        var index = TaskReferenceIndex.Build(board.Cards);
        var ordered = LaneSortApplier.PickupOrder(board.Cards.Where(card =>
            RemoteDispatchEligibility.IsClaimableReady(card, board.Projects[card.ProjectName], Host, Host,
                false, index, board.Integration)), name => board.Projects[name]);
        var claimed = new List<TaskInfo>();
        var current = board;
        foreach (var card in ordered)
        {
            if (!HostCapacityPolicy.Decide(new HostCapacityTargets(current.Slots, 80, RunnerRampStrategies.Balanced),
                    new HostAdmissionFacts(current.Occupied, Epoch)).Admitted) continue;
            if (!ProjectExecutionPolicy.EvaluateProjectSlot(ProjectExecutionPolicy.ProjectOccupancy(current.Cards),
                    card.ProjectName, current.Projects[card.ProjectName]).HasSlot) continue;
            claimed.Add(card);
            current = Advance(current, [card]);
        }
        return claimed;
    }

    private static Board Advance(Board board, IReadOnlyCollection<TaskInfo> claims) => board with
    {
        Occupied = board.Occupied + claims.Count,
        Cards = board.Cards.Select(c => claims.Any(p => p.Key == c.Key) ? c with { State = TaskStates.Progress } : c).ToArray(),
    };

    // Independent oracle: do not use the production fulfillment, cycle, slot or ordering helpers here.
    private static bool ModelEligible(Board board, TaskInfo card)
    {
        var location = board.Projects[card.ProjectName].ExecutionLocation;
        if (card.State != TaskStates.Ready || location is not (Host or "class:linux") || OnCycle(board, card.Key!)) return false;
        return ModelDependenciesFulfilled(board, card);
    }

    // Completed/archive are the existing completion boundary; live deliveries require target-branch integration.
    private static bool ModelDependenciesFulfilled(Board board, TaskInfo card) =>
        Dependencies(card).All(key => board.Cards.FirstOrDefault(c => c.Key == key) is { } target
            && (target.State is TaskStates.Completed or TaskStates.Archive
                || board.Integration.TryGetValue(target.TaskKey, out var integration)
                && integration.Status == IntegrationStatuses.Integrated && integration.IntegrationBranch == "develop"));

    private static bool ModelProjectSlot(Board board, TaskInfo card) =>
        board.Projects[card.ProjectName].ExecutionLocation != "class:linux"
        || board.Cards.Count(c => c.ProjectName == card.ProjectName && c.State == TaskStates.Progress)
            < board.Projects[card.ProjectName].MaxParallelism;

    private static IEnumerable<TaskInfo> ModelOrder(IEnumerable<TaskInfo> cards) => cards
        .OrderBy(c => c.Order == 999 ? 1 : 0).ThenBy(c => c.Order)
        .ThenByDescending(c => c.Order == 999 ? c.EnteredLaneAt : Epoch)
        .ThenByDescending(c => int.Parse(c.Key!.Split('-')[1]));

    private static IEnumerable<string> Dependencies(TaskInfo card) => card.References?.DependsOn.Select(d => d.Key) ?? [];

    private static bool OnCycle(Board board, string start)
    {
        var visited = new HashSet<string>();
        var pending = new Queue<string>(Dependencies(board.Cards.Single(c => c.Key == start)));
        while (pending.TryDequeue(out var key))
        {
            if (key == start) return true;
            if (!visited.Add(key)) continue;
            var target = board.Cards.FirstOrDefault(c => c.Key == key);
            if (target is not null) foreach (var next in Dependencies(target)) pending.Enqueue(next);
        }
        return false;
    }

    private static Board Generate(int seed)
    {
        var random = new Random(seed);
        var projects = Enumerable.Range(0, 3).ToDictionary(i => "project-" + i, i => new ProjectSettings
        {
            PickupMode = PickupModes.Auto,
            ExecutionLocation = i == 0 ? Host : i == 1 ? "host-b" : "class:linux",
            MaxParallelism = random.Next(1, 5),
        });
        var cards = Enumerable.Range(0, TaskStates.All.Length + random.Next(12, 32)).Select(i => new TaskInfo
        {
            Id = "card-" + i, Key = "CARD-" + i, TaskKey = "task-" + i,
            ProjectName = "project-" + random.Next(3), Agent = AgentTypes.Codex,
            State = i < TaskStates.All.Length ? TaskStates.All[i] : TaskStates.All[random.Next(TaskStates.All.Length)],
            Order = random.Next(3) == 0 ? random.Next(4) : 999,
            EnteredLaneAt = Epoch.AddMinutes(random.Next(5)), CreatedAt = Epoch.AddDays(-1),
        }).ToArray();
        for (var i = 0; i < cards.Length; i++)
        {
            // Acyclic random edges first; each seed also plants a chain, diamond and invalid cycle below.
            var deps = Enumerable.Range(0, i).Where(_ => random.Next(10) == 0).Select(j => "CARD-" + j).ToArray();
            cards[i] = WithDependencies(cards[i], deps);
        }
        cards[0] = WithDependencies(cards[0] with { State = TaskStates.Ready, ProjectName = "project-0" },
            seed % 4 == 0 ? "CARD-0" : "CARD-1");
        cards[1] = WithDependencies(cards[1] with { State = TaskStates.Ready, ProjectName = "project-0" }, "CARD-0");
        cards[2] = WithDependencies(cards[2], "CARD-3", "CARD-4");
        cards[3] = WithDependencies(cards[3], "CARD-5");
        cards[4] = WithDependencies(cards[4], "CARD-5");
        cards[5] = WithDependencies(cards[5], "CARD-6");
        cards[6] = WithDependencies(cards[6]);
        cards[7] = WithDependencies(cards[7] with { State = TaskStates.Ready, ProjectName = "project-0" });
        cards[8] = WithDependencies(cards[8] with
        {
            State = TaskStates.Escalated,
            ParkedBlocker = new("operator-decision", "manual", "Waiting for operator", Epoch, 0,
                "Parked predecessor", "blocked", Epoch, "Still blocked"),
        });
        cards[9] = WithDependencies(cards[9] with { State = TaskStates.Ready }, "CARD-8");
        // Ensure all lanes remain represented even when the planted graph overrides their initial cards.
        cards = cards.Concat(TaskStates.All.Select((lane, i) => new TaskInfo
        {
            Id = "lane-" + i, Key = "CARD-" + (100 + i), TaskKey = "lane-" + i,
            State = lane, ProjectName = "project-1", Agent = AgentTypes.Codex,
            CreatedAt = Epoch, EnteredLaneAt = Epoch,
        })).OrderBy(_ => random.Next()).ToArray();
        var integration = cards.ToDictionary(c => c.TaskKey, c => new TaskIntegrationStatus
        {
            Status = random.Next(3) == 0 ? IntegrationStatuses.Integrated : IntegrationStatuses.Pending,
            IntegrationBranch = random.Next(4) == 0 ? "other-branch" : "develop",
        });
        var slots = random.Next(1, 7);
        return new(cards, integration, projects, slots, random.Next(slots + 2));
    }

    private static TaskInfo WithDependencies(TaskInfo card, params string[] keys) => card with
    {
        References = new TaskReferences { DependsOn = keys.Select(key => new TaskDependencyReference(key)).ToList() },
    };

    // Greedy structural shrinking removes cards and edges while preserving failure. Removed targets stay unresolved.
    // The failure includes the seed and full reduced input so it can become a named plain regression.
    private static Board Shrink(Board board, Func<Board, bool> fails)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var card in board.Cards)
            {
                var smaller = board with { Cards = board.Cards.Where(c => c.Key != card.Key).ToArray() };
                if (!fails(smaller)) continue;
                board = smaller;
                changed = true;
            }
            foreach (var card in board.Cards)
            foreach (var dependency in Dependencies(card).ToArray())
            {
                var smaller = board with { Cards = board.Cards.Select(c => c.Key == card.Key
                    ? WithDependencies(c, Dependencies(c).Where(d => d != dependency).ToArray()) : c).ToArray() };
                if (!fails(smaller)) continue;
                board = smaller;
                changed = true;
            }
        } while (changed);
        for (var slots = 1; slots < board.Slots; slots++)
        {
            var smaller = board with { Slots = slots };
            if (fails(smaller)) { board = smaller; break; }
        }
        for (var occupied = 0; occupied < board.Occupied; occupied++)
        {
            var smaller = board with { Occupied = occupied };
            if (fails(smaller)) { board = smaller; break; }
        }
        return board with { Integration = board.Integration.Where(pair => board.Cards.Any(c => c.TaskKey == pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value) };
    }
}
