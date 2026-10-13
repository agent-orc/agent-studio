using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

// clock-independent: throwaway replay of a captured board, removed before commit.
public sealed class ZzReplayTmpTests(ITestOutputHelper output)
{
    [Fact]
    public void Replay()
    {
        var path = Environment.GetEnvironmentVariable("REPLAY_CARDS");
        if (path is null) return;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        var cards = JsonSerializer.Deserialize<TaskInfo[]>(File.ReadAllText(path), options)!;
        var list = BoardCardSummary.SerializeList(cards);
        var hr = cards.Where(c => c.State == TaskStates.HumanReview).Select(c => BoardCardSummary.Serialize(c).Length).ToArray();
        output.WriteLine($"REPLAY cards={cards.Length} list={list.Length} avg={list.Length / (double)cards.Length:F0} hr={hr.Length} hrAvg={hr.Average():F0} hrMax={hr.Max()}");
        File.WriteAllBytes(path + ".out.json", list);
    }
}
