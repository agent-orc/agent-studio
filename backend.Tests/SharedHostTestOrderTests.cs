using Xunit;

namespace AgentStudio.Tests;

public sealed class SharedHostTestOrderTests
{
    /// <summary>xUnit's default order mixes the rows of a theory with other methods.</summary>
    private static readonly string[] Standard = ["a1", "b1", "a2", "c1", "b2", "d1", "a3", "e1", "f1", "g1", "h1", "i1"];

    private static string Method(string row) => row[..1];

    private static IReadOnlyList<string> Arrange(string? mode) => SharedHostTestOrder.Arrange(Standard, Method, mode);

    /// <summary>The methods in the order xUnit runs them: each where its first row appears.</summary>
    private static string[] Methods(IEnumerable<string> rows) => rows.Select(Method).Distinct().ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_choice_the_default_order_stays(string? mode)
        => Assert.Equal(Standard, Arrange(mode));

    [Fact]
    public void Reverse_runs_the_last_method_first_and_its_rows_backwards()
    {
        var reversed = Arrange("reverse");

        Assert.Equal(Enumerable.Reverse(Methods(Standard)), Methods(reversed));
        Assert.Equal(new[] { "a3", "a2", "a1" }, reversed.Where(row => Method(row) == "a"));
        Assert.Equal(Standard.Order(), reversed.Order());
    }

    [Fact]
    public void A_seed_gives_the_same_new_order_every_time()
    {
        var first = Arrange("random:7");

        Assert.Equal(first, Arrange("random:7"));
        Assert.NotEqual(Methods(Standard), Methods(first));
        Assert.NotEqual(first, Arrange("random:8"));
        Assert.Equal(Standard.Order(), first.Order());
    }

    [Fact]
    public void A_shuffled_order_keeps_the_rows_of_a_method_together()
    {
        var methods = Arrange("random:7").Select(Method).ToArray();

        var runs = methods.Where((method, index) => index == 0 || methods[index - 1] != method).Count();
        Assert.Equal(methods.Distinct().Count(), runs);
    }

    [Theory]
    [InlineData("random")]
    [InlineData("random:seven")]
    [InlineData("Reverse")]
    public void An_unknown_choice_fails_instead_of_running_the_default_order(string mode)
        => Assert.Throws<InvalidOperationException>(() => Arrange(mode));
}
