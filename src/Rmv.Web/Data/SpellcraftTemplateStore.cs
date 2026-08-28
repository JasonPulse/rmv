using Microsoft.EntityFrameworkCore;
using Rmv.Web.Tools.Spellcraft;

namespace Rmv.Web.Data;

/// <summary>
/// What a save attempt did. NeedsOverwrite is the interesting one: the member is
/// at the cap, so the page offers the list of templates to replace instead of
/// reporting a dead end.
/// </summary>
public sealed record TemplateSaveOutcome(
    bool Ok, SpellcraftTemplate? Template, string? Error, bool NeedsOverwrite)
{
    public static TemplateSaveOutcome Saved(SpellcraftTemplate t) => new(true, t, null, false);

    public static TemplateSaveOutcome Fail(string error) => new(false, null, error, false);

    public static TemplateSaveOutcome AtCap() => new(
        false,
        null,
        $"You already have {SpellcraftTemplate.MaxPerMember} saved templates. "
        + "Pick one to overwrite, or delete one first.",
        true);
}

/// <summary>
/// Saved spellcraft templates, scoped to their owner.
///
/// Every method takes the caller's own member id and filters on it, so an id from
/// somebody else's page is simply not found. That scoping is the authorisation
/// check, the same way CharacterService does it; there is no separate "is this
/// yours" branch that could be forgotten.
///
/// The cap is enforced here and in the schema, not in the form. A hidden field
/// saying which ordinal to use, or a save button the page disabled, would both be
/// things the caller controls.
/// </summary>
public sealed class SpellcraftTemplateStore(
    RmvDbContext db, ILogger<SpellcraftTemplateStore> log)
{
    public Task<List<SpellcraftTemplate>> ListAsync(int memberId, CancellationToken ct) =>
        db.SpellcraftTemplates
            .Where(t => t.MemberId == memberId)
            .OrderBy(t => t.Ordinal)
            .AsNoTracking()
            .ToListAsync(ct);

    /// <summary>
    /// Saves a design under a name, either into a free slot or over one the member
    /// already has.
    ///
    /// overwriteId is looked up scoped to the member, so passing somebody else's
    /// template id reports the same "not one of yours" as passing a number that
    /// does not exist.
    /// </summary>
    public async Task<TemplateSaveOutcome> SaveAsync(
        int memberId, string? rawName, SpellcraftDesign design, int? overwriteId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(design);

        var name = (rawName ?? "").Trim();
        if (name.Length == 0)
        {
            return TemplateSaveOutcome.Fail("Give the template a name.");
        }

        if (name.Length > SpellcraftTemplate.MaxNameLength)
        {
            return TemplateSaveOutcome.Fail(
                $"That name is too long. {SpellcraftTemplate.MaxNameLength} characters at most.");
        }

        var encoded = design.Encode();
        if (encoded.Length > SpellcraftDesign.MaxEncodedLength)
        {
            return TemplateSaveOutcome.Fail("That design is too large to save.");
        }

        var now = DateTimeOffset.UtcNow;

        if (overwriteId is { } id)
        {
            var existing = await db.SpellcraftTemplates
                .FirstOrDefaultAsync(t => t.Id == id && t.MemberId == memberId, ct);

            if (existing is null)
            {
                return TemplateSaveOutcome.Fail("That template is not one of yours.");
            }

            existing.Name = name;
            existing.Design = encoded;
            existing.SavedAt = now;
            await db.SaveChangesAsync(ct);

            return TemplateSaveOutcome.Saved(existing);
        }

        var taken = await db.SpellcraftTemplates
            .Where(t => t.MemberId == memberId)
            .Select(t => t.Ordinal)
            .ToListAsync(ct);

        if (FreeOrdinal(taken) is not { } ordinal)
        {
            return TemplateSaveOutcome.AtCap();
        }

        var row = new SpellcraftTemplate
        {
            MemberId = memberId,
            Ordinal = ordinal,
            Name = name,
            Design = encoded,
            SavedAt = now,
        };

        db.SpellcraftTemplates.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The unique index or the check constraint caught a race between two
            // submits. Which message is truthful depends on whether the member is
            // now at the cap, so ask rather than guess.
            db.Entry(row).State = EntityState.Detached;
            log.LogInformation(ex, "Concurrent spellcraft template save for member {Member}.", memberId);

            var count = await db.SpellcraftTemplates.CountAsync(t => t.MemberId == memberId, ct);
            return count >= SpellcraftTemplate.MaxPerMember
                ? TemplateSaveOutcome.AtCap()
                : TemplateSaveOutcome.Fail("Could not save that just now. Try again.");
        }

        return TemplateSaveOutcome.Saved(row);
    }

    /// <summary>
    /// Removes one of the caller's own templates. Returns false for an id that is
    /// not theirs, which is the same answer as for an id that does not exist.
    /// </summary>
    public async Task<SpellcraftTemplate?> DeleteAsync(int memberId, int id, CancellationToken ct)
    {
        var row = await db.SpellcraftTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.MemberId == memberId, ct);

        if (row is null)
        {
            return null;
        }

        db.SpellcraftTemplates.Remove(row);
        await db.SaveChangesAsync(ct);

        return row;
    }

    /// <summary>
    /// One of the caller's own templates, or null. The member filter is the
    /// authorisation check.
    /// </summary>
    public Task<SpellcraftTemplate?> FindAsync(int memberId, int id, CancellationToken ct) =>
        db.SpellcraftTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.MemberId == memberId, ct);

    /// <summary>
    /// The lowest ordinal the member is not using, or null when all of them are
    /// taken. Reusing a freed ordinal is what keeps deleting the third template
    /// and saving a new one from needing a renumber.
    /// </summary>
    private static int? FreeOrdinal(IReadOnlyCollection<int> taken)
    {
        for (var i = 1; i <= SpellcraftTemplate.MaxPerMember; i++)
        {
            if (!taken.Contains(i))
            {
                return i;
            }
        }

        return null;
    }
}
