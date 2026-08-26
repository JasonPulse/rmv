using System.Text;
using Rmv.Web.Tools;

namespace Rmv.Web.Tests;

public class RollParserTests
{
    private static RollReport Parse(string text) => RollParser.Parse(new StringReader(text));

    // --- the shapes a real log actually contains ---------------------------

    [Theory]
    [InlineData("[Sat Jan 01 12:00:00 2011] Bilbo picks a random number between 1 and 100: 87")]
    [InlineData("[12:00:00] Bilbo picks a random number between 1 and 100: 87")]
    [InlineData("Bilbo picks a random number between 1 and 100: 87")]
    [InlineData("Bilbo picks a random number between 1 and 100: 87.")]
    [InlineData("[12:00:00] Bilbo  picks  a  random  number  between  1  and  100:  87")]
    public void Accepts_the_line_shapes_a_log_contains(string line)
    {
        var report = Parse(line);

        Assert.Equal(1, report.RollsFound);
        Assert.Equal(87, report.Winner!.Value);
        Assert.Equal("Bilbo", report.Winner.Names.Single());
    }

    [Fact]
    public void Accepts_the_singular_form_used_for_your_own_roll()
    {
        // The game writes "You pick", not "You picks".
        var report = Parse("[12:00:00] You pick a random number between 1 and 100: 42");

        Assert.Equal("You", report.Winner!.Names.Single());
        Assert.Equal(42, report.Winner.Value);
    }

    [Fact]
    public void Groups_by_value_highest_first()
    {
        var report = Parse("""
            [12:00:00] Bilbo picks a random number between 1 and 100: 12
            [12:00:01] Frodo picks a random number between 1 and 100: 99
            [12:00:02] Samwise picks a random number between 1 and 100: 55
            [12:00:03] Merry picks a random number between 1 and 100: 99
            """);

        Assert.Equal([99, 55, 12], report.Groups.Select(g => g.Value));
        // A tie lists everyone, in log order, which is what settles an argument.
        Assert.Equal(["Frodo", "Merry"], report.Winner!.Names);
    }

    [Fact]
    public void Keeps_a_reroll_as_two_entries()
    {
        var report = Parse("""
            [12:00:00] Bilbo picks a random number between 1 and 100: 4
            [12:00:01] Bilbo picks a random number between 1 and 100: 71
            """);

        Assert.Equal(2, report.RollsFound);
        Assert.Equal(71, report.Winner!.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Accepts_both_ends_of_the_range(int value)
    {
        var report = Parse($"Bilbo picks a random number between 1 and 100: {value}");

        Assert.Equal(value, report.Winner!.Value);
    }

    // --- what must be ignored ---------------------------------------------

    [Theory]
    // Out of range, which the PHP would have accepted as a string key.
    [InlineData("Bilbo picks a random number between 1 and 100: 101")]
    [InlineData("Bilbo picks a random number between 1 and 100: 999")]
    // A different range is a different roll; not ours to report.
    [InlineData("Bilbo picks a random number between 1 and 1000: 87")]
    [InlineData("Bilbo picks a random number between 5 and 100: 87")]
    // Non-numeric value.
    [InlineData("Bilbo picks a random number between 1 and 100: abc")]
    [InlineData("Bilbo picks a random number between 1 and 100:")]
    // Trailing content means it is not a roll line.
    [InlineData("Bilbo picks a random number between 1 and 100: 87 and then died")]
    // Ordinary chat that merely mentions rolling.
    [InlineData("[12:00:00] Bilbo says, \"picks a random number between 1 and 100: 87\"")]
    [InlineData("[12:00:00] Bilbo hits the dragon for 87 damage.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Ignores_anything_not_shaped_like_a_roll(string line)
    {
        Assert.Equal(0, Parse(line).RollsFound);
    }

    [Theory]
    // Names in DAoC are alphabetic. Anything else is not a name, and these are
    // exactly the payloads the PHP would have echoed into the page unescaped.
    [InlineData("<script>alert(1)</script> picks a random number between 1 and 100: 87")]
    [InlineData("<img src=x onerror=alert(1)> picks a random number between 1 and 100: 87")]
    [InlineData("Bilbo\"onmouseover=\"alert(1) picks a random number between 1 and 100: 87")]
    [InlineData("../../etc/passwd picks a random number between 1 and 100: 87")]
    [InlineData("Bilbo';DROP TABLE deployments;-- picks a random number between 1 and 100: 87")]
    [InlineData("Bilbo123 picks a random number between 1 and 100: 87")]
    [InlineData("A_very_long_name_well_past_the_cap picks a random number between 1 and 100: 87")]
    public void Rejects_names_that_are_not_names(string line)
    {
        Assert.Equal(0, Parse(line).RollsFound);
    }

    [Fact]
    public void A_hostile_name_cannot_reach_the_output_even_when_the_rest_matches()
    {
        // The one thing that matters: every name in the report is renderable.
        var report = Parse("""
            <script>x</script> picks a random number between 1 and 100: 90
            Frodo picks a random number between 1 and 100: 80
            """);

        var names = report.Groups.SelectMany(g => g.Names).ToList();
        Assert.Equal(["Frodo"], names);
        Assert.All(names, n => Assert.Matches("^[A-Za-z]{1,24}$", n));
    }

    // --- robustness --------------------------------------------------------

    [Fact]
    public void Handles_crlf_line_endings()
    {
        // The original regex depended on a literal \r, so a unix-saved log
        // silently produced nothing. Line endings must not matter.
        var report = Parse("Bilbo picks a random number between 1 and 100: 87\r\nFrodo picks a random number between 1 and 100: 12\r\n");

        Assert.Equal(2, report.RollsFound);
    }

    [Fact]
    public void Survives_invalid_utf8_bytes()
    {
        var bytes = "Bilbo picks a random number between 1 and 100: 87\n"u8.ToArray()
            .Concat(new byte[] { 0xFF, 0xFE, 0xC0, 0x80 })
            .Concat("\nFrodo picks a random number between 1 and 100: 12\n"u8.ToArray())
            .ToArray();

        var report = RollParser.Parse(new MemoryStream(bytes));

        Assert.Equal(2, report.RollsFound);
    }

    [Fact]
    public void Stops_at_the_roll_limit_and_says_so()
    {
        var many = string.Join('\n',
            Enumerable.Repeat("Bilbo picks a random number between 1 and 100: 50", RollParser.MaxRolls + 50));

        var report = Parse(many);

        Assert.True(report.HitRollLimit);
        Assert.True(report.Truncated);
        Assert.Equal(RollParser.MaxRolls, report.RollsFound);
    }

    [Fact]
    public void A_very_long_single_line_does_not_hang()
    {
        // Guards against catastrophic backtracking on adversarial input.
        var line = new string('A', 200_000) + " picks a random number between 1 and 100: 87";

        var report = Parse(line);

        Assert.Equal(0, report.RollsFound);
    }

    [Fact]
    public void Empty_input_is_an_empty_report_not_an_error()
    {
        var report = Parse("");

        Assert.Equal(0, report.RollsFound);
        Assert.Null(report.Winner);
        Assert.False(report.Truncated);
    }
}
