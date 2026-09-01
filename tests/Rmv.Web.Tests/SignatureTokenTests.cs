using Rmv.Web.Data;
using Rmv.Web.Signature;

namespace Rmv.Web.Tests;

/// <summary>
/// Filling a template in.
///
/// The template is what a member types, so it is untrusted text that happens to be
/// ours to interpret. Nothing here can throw on it: every shape of stray percent
/// has an answer, because the alternative is a signature that 500s inside an image
/// tag on somebody else's forum.
/// </summary>
public class SignatureTokenTests
{
    private static Character Char(Action<Character>? edit = null)
    {
        var c = new Character
        {
            Id = 1,
            Name = "Property",
            Level = 50,
            Class = "Skald",
            Race = "Norseman",
            Realm = "Midgard",
            Guild = "Results May Vary",
            RealmRank = "8L0",
            Score = 1_234_567,
            Kills = 12_345,
            Deaths = 678,
            LastOnline = "2026-05-01",
            AddedAt = new DateTimeOffset(2001, 10, 10, 0, 0, 0, TimeSpan.Zero),
            GamePresenceId = 7,
            Game = new GamePresence { Id = 7, Game = "Dark Age of Camelot" },
        };

        edit?.Invoke(c);
        return c;
    }

    private static SignatureSubject Subject(params Character[] roster) =>
        SignatureData.Subject(
            new Member { DisplayName = "property_x", Alias = "Property" },
            roster,
            roster.Length > 0 ? roster[0].Id : null);

    private static string Resolve(string template, params Character[] roster) =>
        SignatureTokens.Resolve(template, Subject(roster.Length > 0 ? roster : [Char()]));

    [Fact]
    public void The_v1_default_template_still_reads_the_same()
    {
        // Feature parity, in one assertion. This is the string v1 shipped as its
        // default, with its two-letter tokens swapped for readable ones, and it has
        // to come out looking like a signature people recognise.
        var line = Resolve("%Name%%SP%Level %Level% %Race% %Class%%SP%%Guild%%SP%%Rank%%SP%%Score% points");

        Assert.Equal(
            "Property - Level 50 Norseman Skald - Results May Vary - 8L0 - 1,234,567 points",
            line);
    }

    [Fact]
    public void His_example_reads_as_he_wrote_it()
    {
        var roster = new[]
        {
            Char(),
            Char(c => { c.Id = 2; c.GamePresenceId = 8; c.Level = 99; }),
            Char(c => { c.Id = 3; c.GamePresenceId = 8; c.Level = 80; }),
        };

        var line = SignatureTokens.Resolve(
            "%User% has played %AllChars% characters in %AllGames% games", Subject(roster));

        Assert.Equal("Property has played 3 characters in 2 games", line);
    }

    [Fact]
    public void Numbers_are_grouped_the_way_a_herald_writes_them()
    {
        Assert.Equal("1,234,567", Resolve("%Score%"));
        Assert.Equal("12,345", Resolve("%Kills%"));
        // A level is a number too, and nobody wants "50" grouped differently.
        Assert.Equal("50", Resolve("%Level%"));
    }

    [Fact]
    public void A_year_is_not_grouped()
    {
        // 2,001 would be an odd thing for a signature to claim.
        Assert.Equal("2001", Resolve("%Since%"));
    }

    [Fact]
    public void The_score_total_adds_up_measures_that_are_not_the_same_thing()
    {
        // Realm points, job levels and achievement points all live in one column, so
        // the total is deliberately apples and oranges. It is the "how much of this
        // have I done" number he asked for.
        var roster = new[]
        {
            Char(c => c.Score = 1_000_000),
            Char(c => { c.Id = 2; c.Score = 500; c.GamePresenceId = 9; }),
            Char(c => { c.Id = 3; c.Score = null; c.GamePresenceId = 9; }),
        };

        Assert.Equal("1,000,500", SignatureTokens.Resolve("%AllScore%", Subject(roster)));
        Assert.Equal("3", SignatureTokens.Resolve("%AllChars%", Subject(roster)));
        Assert.Equal("2", SignatureTokens.Resolve("%AllGames%", Subject(roster)));
    }

    [Fact]
    public void An_absent_value_leaves_nothing_rather_than_a_zero()
    {
        // A character with no guild has no guild, and "0" or "null" in a signature
        // reads as a bug in the site rather than a gap in the herald.
        var line = Resolve("[%Guild%][%Rank%][%Deaths%]", Char(c =>
        {
            c.Guild = null;
            c.RealmRank = null;
            c.Deaths = null;
        }));

        Assert.Equal("[][][]", line);
    }

    [Fact]
    public void A_character_token_on_an_element_bound_to_nobody_is_empty()
    {
        // How a line of pure member totals works: it draws no character, so asking
        // for one is nothing rather than a failure.
        var subject = SignatureData.Subject(
            new Member { DisplayName = "Property" }, [Char()], characterId: null);

        Assert.Equal("Property plays 1 characters",
            SignatureTokens.Resolve("%User% plays %AllChars% characters", subject));
        Assert.Equal("", SignatureTokens.Resolve("%Name%", subject));
    }

    [Fact]
    public void A_character_that_is_not_on_the_roster_binds_to_nobody()
    {
        // Somebody else's character id in a design, or one they deleted since.
        var subject = SignatureData.Subject(
            new Member { DisplayName = "Property" }, [Char()], characterId: 999);

        Assert.Null(subject.Character);
        Assert.Equal("", SignatureTokens.Resolve("%Name%", subject));
    }

    [Theory]
    // Every shape of stray percent, none of which may throw.
    [InlineData("100%", "100%")]
    [InlineData("%", "%")]
    [InlineData("%%", "%")]
    [InlineData("50%% of the time", "50% of the time")]
    [InlineData("%Name", "%Name")]
    [InlineData("Name%", "Name%")]
    [InlineData("%%Name%%", "%Name%")]
    [InlineData("%unknown%", "%unknown%")]
    [InlineData("%%%", "%%")]
    [InlineData("", "")]
    public void Percent_signs_that_are_not_tokens(string template, string expected)
    {
        Assert.Equal(expected, Resolve(template));
    }

    [Fact]
    public void A_mistyped_token_is_left_where_it_can_be_seen()
    {
        // Rather than silently blank, which leaves somebody staring at a gap.
        Assert.Equal("%Realmm% and Midgard", Resolve("%Realmm% and %Realm%"));
    }

    [Fact]
    public void Token_names_are_case_insensitive()
    {
        Assert.Equal("Property", Resolve("%name%"));
        Assert.Equal("Property", Resolve("%NAME%"));
    }

    [Fact]
    public void Nothing_a_member_can_type_makes_it_throw()
    {
        // Fuzzed rather than reasoned about, because this string comes from a form
        // and ends up inside an image tag on a forum that is not ours.
        var random = new Random(20260901);
        var alphabet = "%NameLevel {}[]()\\\"'<>&;:0123456789 \t\n".ToCharArray();

        for (var i = 0; i < 2000; i++)
        {
            var length = random.Next(0, 40);
            var template = new string(
                Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

            var line = SignatureTokens.Resolve(template, Subject(Char()));

            Assert.NotNull(line);
        }
    }

    [Fact]
    public void Every_token_has_a_description_and_an_example()
    {
        // The editor's palette renders these, so a token with neither is a blank
        // entry somebody has to guess at.
        foreach (var token in SignatureTokens.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(token.Description), token.Name);
            Assert.False(string.IsNullOrWhiteSpace(token.Example), token.Name);
            Assert.DoesNotContain("%", token.Name);
        }
    }

    [Fact]
    public void Every_token_resolves_for_every_subject_shape()
    {
        // Including the empty roster, which is a new member with a default design.
        var shapes = new[]
        {
            Subject(Char()),
            SignatureData.Subject(new Member { DisplayName = "Nobody" }, [], null),
        };

        foreach (var subject in shapes)
        {
            foreach (var token in SignatureTokens.All)
            {
                var value = SignatureTokens.Resolve($"%{token.Name}%", subject);
                Assert.NotNull(value);
                Assert.DoesNotContain("%", value);
            }
        }
    }

    [Fact]
    public void No_two_tokens_share_a_name()
    {
        var names = SignatureTokens.All.Select(t => t.Name.ToLowerInvariant()).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // --- what a character sheet says, and what a herald says ------------------

    [Fact]
    public void The_character_group_is_the_character_sheet_and_nothing_else()
    {
        // What the add form asks for, plus the two facts a character cannot be
        // without. Everything a herald fills in moved out, because a member with a
        // hand-typed character was being offered %Rank% and %Score% for a game whose
        // server closed in 2004.
        var sheet = SignatureTokens.All
            .Where(t => t.Scope == TokenScope.Character)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(["Class", "Game", "Level", "Name", "Race"], sheet);

        // And none of them is a column a herald names in its own words.
        Assert.All(sheet, name => Assert.Null(Rmv.Web.Herald.SheetColumns.Field(name)));
    }

    [Fact]
    public void A_hand_typed_character_draws_its_own_race()
    {
        // Race is on the add form now, so it draws for a game with no herald.
        var typed = new Character
        {
            Id = 4,
            Name = "Sigrun",
            Class = "Warden",
            Race = "Firbolg",
            Level = 44,
            Source = CharacterSource.Manual,
            AddedAt = DateTimeOffset.UtcNow,
            GamePresenceId = 9,
            Game = new GamePresence { Id = 9, Game = "Shadowbane" },
        };

        Assert.Equal(
            "Sigrun, Firbolg Warden 44, Shadowbane",
            Resolve("%Name%, %Race% %Class% %Level%, %Game%", typed));
    }

    [Fact]
    public void A_shared_column_draws_without_any_herald_being_built()
    {
        // The reason SheetColumns is a fixed list rather than a set that fills up as
        // adapters are constructed. Adapters are scoped services; a signature served
        // from its stored render never builds one. A resolver that learned these
        // names from adapter construction would draw them on the request that
        // happened to have built an adapter and leave "%Score%" as typed on the next.
        //
        // Nothing in this test touches the registry.
        Assert.Equal(
            "Results May Vary - 8L0 - 1,234,567 - 12,345/678 - Midgard - 2026-05-01",
            Resolve("%Guild%%SP%%Rank%%SP%%Score%%SP%%Kills%/%Deaths%%SP%%Realm%%SP%%Seen%"));
    }

    [Fact]
    public void A_shared_column_a_character_has_nothing_in_draws_nothing()
    {
        var bare = new Character
        {
            Id = 5, Name = "Halvard", AddedAt = DateTimeOffset.UtcNow, GamePresenceId = 9,
        };

        Assert.Equal("[][][]", Resolve("[%Guild%][%Score%][%Seen%]", bare));
    }
}
