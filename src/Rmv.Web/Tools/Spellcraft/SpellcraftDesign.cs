using System.Text;
using System.Text.RegularExpressions;

namespace Rmv.Web.Tools.Spellcraft;

/// <summary>
/// What somebody typed into the form, or what a saved template holds. Every field
/// is a code that may not exist and a number that may be nonsense.
///
/// Nothing calculates from this. It is turned into a ResolvedDesign first, and
/// only that carries rows the tables actually contain, which is what stops a
/// forged form reaching the arithmetic.
/// </summary>
public sealed partial record SpellcraftDesign(
    string RealmCode,
    string SlotCode,
    int ItemLevel,
    IReadOnlyList<string> GemCodes)
{
    /// <summary>
    /// Bounds the database column. A version marker, three codes and eight gem
    /// codes at the length below cannot approach it.
    /// </summary>
    public const int MaxEncodedLength = 256;

    /// <summary>Long enough for any code the tables use, short enough to bound the encoding.</summary>
    public const int MaxCodeLength = 24;

    /// <summary>Bumped if the encoding ever changes shape. An unknown version decodes to nothing.</summary>
    private const string Version = "1";

    public static readonly SpellcraftDesign Empty = new("", "", 0, []);

    // Codes are ours, not the visitor's, so they can be this narrow. Anything
    // else fails to decode rather than being sanitised on the way out.
    [GeneratedRegex(@"^[a-z0-9-]{1,24}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CodeShape();

    /// <summary>
    /// The design as one string, which is how a template stores it.
    ///
    /// Pipes separate the fields and commas separate the gems, so neither can
    /// appear in a code. CodeShape is what guarantees that, and decoding checks it
    /// again rather than trusting what was written.
    /// </summary>
    public string Encode()
    {
        var sb = new StringBuilder(MaxEncodedLength);
        sb.Append(Version).Append('|')
          .Append(RealmCode).Append('|')
          .Append(SlotCode).Append('|')
          .Append(ItemLevel).Append('|')
          .Append(string.Join(',', GemCodes));

        return sb.ToString();
    }

    /// <summary>
    /// Reads back what Encode wrote. False for anything else, including a row
    /// written by a future version of the format.
    /// </summary>
    public static bool TryDecode(string? text, out SpellcraftDesign design)
    {
        design = Empty;

        if (string.IsNullOrEmpty(text) || text.Length > MaxEncodedLength)
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length != 5 || parts[0] != Version)
        {
            return false;
        }

        // The realm may be blank, because "any realm" is a real choice on the form
        // and a template saved that way has to load back. The slot may not: there
        // is no item without one.
        if (!IsCodeOrBlank(parts[1]) || !IsCode(parts[2]) || !int.TryParse(parts[3], out var level))
        {
            return false;
        }

        // An empty gem list is a legitimate design: an item with nothing in it yet.
        var gems = parts[4].Length == 0 ? [] : parts[4].Split(',');
        if (gems.Length > SpellcraftTables.MaxSockets)
        {
            return false;
        }

        foreach (var gem in gems)
        {
            // Blank is an empty socket, which is not the same as an invalid code.
            if (gem.Length > 0 && !IsCode(gem))
            {
                return false;
            }
        }

        design = new SpellcraftDesign(parts[1], parts[2], level, gems);
        return true;
    }

    private static bool IsCode(string value) => CodeShape().IsMatch(value);

    private static bool IsCodeOrBlank(string value) => value.Length == 0 || IsCode(value);
}

/// <summary>
/// One socket after resolution: the gem in it, or why the gem in it does not
/// count. A socket with a problem contributes nothing to any total.
/// </summary>
public sealed record SocketFill(int Index, Gem? Gem, string? Problem)
{
    /// <summary>Empty sockets are fine and are not a problem.</summary>
    public bool Counts => Gem is not null && Problem is null;
}

/// <summary>
/// A design checked against the tables. Every gem here is a row the tables
/// contain, and the slot and level are known good.
///
/// Only SpellcraftTables.Resolve mints one, which is what makes "the calculator
/// only ever sees real rows" an invariant rather than a convention.
/// </summary>
public sealed class ResolvedDesign
{
    internal ResolvedDesign(
        SpellcraftTables tables,
        Realm? realm,
        ItemSlot slot,
        int itemLevel,
        IReadOnlyList<SocketFill> sockets)
    {
        Tables = tables;
        Realm = realm;
        Slot = slot;
        ItemLevel = itemLevel;
        Sockets = sockets;
    }

    public SpellcraftTables Tables { get; }

    public Realm? Realm { get; }

    public ItemSlot Slot { get; }

    public int ItemLevel { get; }

    /// <summary>Exactly as many entries as the slot has sockets.</summary>
    public IReadOnlyList<SocketFill> Sockets { get; }
}

/// <summary>The outcome of checking a design. One or the other, never both.</summary>
public sealed record Resolution(ResolvedDesign? Design, string? Error);

public static class SpellcraftResolver
{
    /// <summary>
    /// Turns form input into something the calculator will accept.
    ///
    /// A bad slot or level is fatal, because there is nothing to show without
    /// them. A bad gem is not: it becomes a problem on its own socket, so the rest
    /// of the item still adds up and the visitor can see which one is wrong.
    ///
    /// Gem codes beyond the slot's socket count are dropped rather than reported.
    /// That is the ordinary case of changing the slot on a filled form, not an
    /// attack, and complaining about it would be noise.
    /// </summary>
    public static Resolution Resolve(this SpellcraftTables tables, SpellcraftDesign design)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(design);

        var realm = tables.FindRealm(design.RealmCode);
        if (realm is null && design.RealmCode.Length > 0)
        {
            return new Resolution(null, "That realm does not exist.");
        }

        var slot = tables.FindSlot(design.SlotCode);
        if (slot is null)
        {
            return new Resolution(null, "Pick an item slot.");
        }

        if (slot.RealmCode is not null && slot.RealmCode != realm?.Code)
        {
            return new Resolution(null, $"{slot.Name} is not a {realm?.Name ?? "realmless"} item.");
        }

        if (design.ItemLevel < tables.MinItemLevel || design.ItemLevel > tables.MaxItemLevel)
        {
            return new Resolution(
                null,
                $"Item level has to be between {tables.MinItemLevel} and {tables.MaxItemLevel}.");
        }

        var sockets = new List<SocketFill>(slot.Sockets);
        for (var i = 0; i < slot.Sockets; i++)
        {
            var code = i < design.GemCodes.Count ? design.GemCodes[i] : "";
            sockets.Add(Fill(tables, realm, slot, i, code));
        }

        return new Resolution(new ResolvedDesign(tables, realm, slot, design.ItemLevel, sockets), null);
    }

    private static SocketFill Fill(
        SpellcraftTables tables, Realm? realm, ItemSlot slot, int index, string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return new SocketFill(index, null, null);
        }

        var gem = tables.FindGem(code);
        if (gem is null)
        {
            return new SocketFill(index, null, "There is no such gem.");
        }

        if (gem.RealmCode is not null && gem.RealmCode != realm?.Code)
        {
            return new SocketFill(index, gem, $"{gem.Name} is not available to {realm?.Name ?? "this realm"}.");
        }

        if (!tables.Accepts(slot, gem))
        {
            var bonus = tables.FindBonus(gem.BonusCode);
            return new SocketFill(index, gem, $"{bonus?.Name ?? gem.Name} cannot go on {slot.Name}.");
        }

        return new SocketFill(index, gem, null);
    }
}
