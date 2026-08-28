using Rmv.Web.Tools.Spellcraft;

namespace Rmv.Web.Tests;

/// <summary>
/// The encoding a saved template is stored as.
///
/// It matters more than it looks: the encoded string is the only thing that
/// crosses from the database back into the calculator, so it is untrusted input
/// on the way in even though we wrote it on the way out. Decoding is a parse, not
/// a cast.
/// </summary>
public class SpellcraftDesignTests
{
    private static SpellcraftDesign Decoded(string text)
    {
        Assert.True(SpellcraftDesign.TryDecode(text, out var design), text);
        return design;
    }

    [Fact]
    public void A_design_survives_a_round_trip()
    {
        var original = new SpellcraftDesign("alb", "chest", 51, ["str-1", "", "dex-3", "body-2"]);

        var back = Decoded(original.Encode());

        Assert.Equal("alb", back.RealmCode);
        Assert.Equal("chest", back.SlotCode);
        Assert.Equal(51, back.ItemLevel);
        Assert.Equal(["str-1", "", "dex-3", "body-2"], back.GemCodes);
    }

    [Fact]
    public void An_item_with_no_gems_in_it_round_trips()
    {
        var back = Decoded(new SpellcraftDesign("mid", "helm", 40, []).Encode());

        Assert.Empty(back.GemCodes);
        Assert.Equal("helm", back.SlotCode);
    }

    [Fact]
    public void Any_realm_round_trips_as_a_blank_realm()
    {
        // "Any realm" is a real choice on the form, so a template saved that way
        // has to load back rather than reading as a corrupt row.
        var back = Decoded(new SpellcraftDesign("", "chest", 51, ["str-1"]).Encode());

        Assert.Equal("", back.RealmCode);
        Assert.Equal("chest", back.SlotCode);
        Assert.Equal(["str-1"], back.GemCodes);
    }

    [Fact]
    public void An_encoded_design_fits_the_column()
    {
        // Every socket full, at the longest code the format allows.
        var longest = new string('a', SpellcraftDesign.MaxCodeLength);
        var gems = Enumerable.Repeat(longest, SpellcraftTables.MaxSockets).ToArray();

        var encoded = new SpellcraftDesign(longest, longest, 999, gems).Encode();

        Assert.True(
            encoded.Length <= SpellcraftDesign.MaxEncodedLength,
            $"{encoded.Length} characters against a column of {SpellcraftDesign.MaxEncodedLength}.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("2|alb|chest|51|str-1")]           // a version this page does not read
    [InlineData("1|alb|chest|51")]                 // a field short
    [InlineData("1|alb|chest|51|str-1|extra")]     // a field long
    [InlineData("1|alb|chest|fifty|str-1")]        // level is not a number
    [InlineData("1|ALB|chest|51|str-1")]           // codes are lower case
    [InlineData("1|alb|ch est|51|str-1")]          // no spaces in a code
    [InlineData("1|alb|chest|51|<script>")]        // not a code by any reading
    [InlineData("1|alb||51|str-1")]                // an item with no slot is not an item
    public void Anything_that_is_not_what_we_wrote_fails_to_decode(string text)
    {
        Assert.False(SpellcraftDesign.TryDecode(text, out _));
    }

    [Fact]
    public void More_gems_than_any_item_has_sockets_fails_to_decode()
    {
        var tooMany = string.Join(',', Enumerable.Repeat("str-1", SpellcraftTables.MaxSockets + 1));

        Assert.False(SpellcraftDesign.TryDecode($"1|alb|chest|51|{tooMany}", out _));
    }

    [Fact]
    public void A_string_longer_than_the_column_fails_to_decode()
    {
        var overlong = "1|alb|chest|51|" + new string('a', SpellcraftDesign.MaxEncodedLength);

        Assert.False(SpellcraftDesign.TryDecode(overlong, out _));
    }

    [Fact]
    public void A_failed_decode_yields_an_empty_design_rather_than_a_half_read_one()
    {
        SpellcraftDesign.TryDecode("1|alb|chest|nope|str-1", out var design);

        Assert.Equal(SpellcraftDesign.Empty, design);
    }
}
