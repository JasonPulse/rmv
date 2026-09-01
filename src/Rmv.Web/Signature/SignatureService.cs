using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;

namespace Rmv.Web.Signature;

/// <summary>What a save did, in the shape the page needs.</summary>
public sealed record SignatureOutcome(bool Ok, Data.Signature? Signature, string? Error)
{
    public static SignatureOutcome Fail(string error) => new(false, null, error);
}

/// <summary>
/// A member's signature: reading it, saving it, and keeping its picture current.
///
/// One place decides when a signature is re-rendered, because getting that wrong is
/// how the old ones ended up either stale or expensive. sig.php re-rendered on every
/// request for the image, which is why a forum page with ten signatures cost ten
/// herald scrapes; the opposite mistake is rendering on a design change only, and
/// then a signature never notices that its character levelled.
///
/// The answer is a digest of everything that goes into a render. Recomputing it is a
/// few string hashes over data the pass has already loaded; rendering is real work.
/// A pass that finds the digest unchanged writes nothing, so the bytes a browser
/// cached stay valid.
/// </summary>
public sealed class SignatureService(
    RmvDbContext db,
    SignatureRenderer renderer,
    SignaturePresets presets,
    ILogger<SignatureService> log)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// The member's signature, creating it with the default design if they have
    /// none.
    ///
    /// Created on first look rather than on approval, so a member who never opens
    /// the page has no row and no rendered PNG sitting in the database.
    /// </summary>
    public async Task<Data.Signature> EnsureAsync(Member member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);

        var existing = await db.Signatures
            .Include(s => s.Image)
            .FirstOrDefaultAsync(s => s.MemberId == member.Id, ct);

        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;

        var signature = new Data.Signature
        {
            MemberId = member.Id,
            Slug = NewSlug(),
            Design = Serialise(await DefaultDesignAsync(member, ct)),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Signatures.Add(signature);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two tabs opened the page at once. The unique index on MemberId settled
            // it, so read back whichever won.
            log.LogInformation(ex, "Signature for member {Member} was created concurrently.", member.Id);
            db.Entry(signature).State = EntityState.Detached;

            var won = await db.Signatures
                .Include(s => s.Image)
                .FirstOrDefaultAsync(s => s.MemberId == member.Id, ct);

            // Nothing won, so the write failed for a reason that was not a race and
            // the caller needs to know rather than getting a half-made signature.
            if (won is null)
            {
                throw;
            }

            return won;
        }

        return signature;
    }

    /// <summary>
    /// The design a member starts with, bound to whichever character they added
    /// first so it says something true rather than drawing a row of empty tokens.
    ///
    /// One place, because both the first look at the page and the Start over button
    /// need the same answer, and two copies of "which character" is how they would
    /// come to differ.
    /// </summary>
    public async Task<SignatureDesign> DefaultDesignAsync(Member member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);

        var first = await db.Characters
            .Where(c => c.MemberId == member.Id)
            .OrderBy(c => c.AddedAt)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync(ct);

        return SignatureDesign.Default(first);
    }

    /// <summary>
    /// Stores a design, after putting it through the clamps, and renders it.
    ///
    /// The design arrives as JSON from a browser, so it is parsed and clamped rather
    /// than stored as sent; see SignatureDesignReader. What gets written is what the
    /// renderer will actually draw, so the editor cannot save something the server
    /// then reinterprets.
    /// </summary>
    public async Task<SignatureOutcome> SaveAsync(
        Member member, string? json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (json is null || json.Length > SignatureLimits.MaxDesignLength)
        {
            return SignatureOutcome.Fail("That design is too large to save.");
        }

        if (SignatureDesignReader.Read(json) is not { } design)
        {
            return SignatureOutcome.Fail("That design could not be read.");
        }

        var owned = await OwnedCharacterIdsAsync(member, ct);
        design = SignatureDesignReader.Clamp(design, owned, presets.Keys);

        var signature = await EnsureAsync(member, ct);

        signature.Design = Serialise(design);
        signature.UpdatedAt = DateTimeOffset.UtcNow;

        await RenderAsync(signature, member, ct);
        await db.SaveChangesAsync(ct);

        return new SignatureOutcome(true, signature, null);
    }

    /// <summary>
    /// Re-renders a member's signature if anything it draws has changed.
    ///
    /// Called after a herald refresh, which is what keeps a signature current
    /// without anyone opening the page. Returns whether the picture actually moved.
    /// </summary>
    public async Task<bool> RefreshAsync(int memberId, CancellationToken ct)
    {
        var signature = await db.Signatures
            .Include(s => s.Image)
            .FirstOrDefaultAsync(s => s.MemberId == memberId, ct);

        if (signature is null)
        {
            return false;
        }

        var member = await db.Members.FirstOrDefaultAsync(m => m.Id == memberId, ct);
        if (member is null)
        {
            return false;
        }

        var changed = await RenderAsync(signature, member, ct);

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    /// <summary>
    /// Renders into the signature's image row, unless nothing that goes into it has
    /// changed.
    ///
    /// The comparison is on SourceVersion, a digest of the design, every string the
    /// elements resolve to, and the background. That is deliberately not a digest of
    /// the rendered bytes: computing those means rendering, which is the work being
    /// avoided.
    /// </summary>
    private async Task<bool> RenderAsync(
        Data.Signature signature, Member member, CancellationToken ct)
    {
        var design = SignatureDesignReader.Read(signature.Design) ?? SignatureDesign.Default(null);

        var roster = await db.Characters
            .Include(c => c.Game)
            .Where(c => c.MemberId == member.Id)
            .OrderBy(c => c.AddedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        var background = await BackgroundAsync(design, member.Id, ct);
        var source = SourceVersionOf(design, member, roster, background);

        if (signature.Image is { } current && current.SourceVersion == source)
        {
            return false;
        }

        var bytes = renderer.Render(design, member, roster, background);
        var version = Digest(bytes);

        if (signature.Image is null)
        {
            signature.Image = new SignatureImage { Signature = signature };
            db.SignatureImages.Add(signature.Image);
        }

        var image = signature.Image;

        // The picture may be identical even when its inputs changed: a character
        // gaining a kill nobody put in a template moves the source digest and not a
        // pixel. Keeping the version means a browser is not asked to refetch.
        if (image.Version != version)
        {
            image.Bytes = bytes;
            image.Version = version;
        }

        image.SourceVersion = source;
        image.RenderedAt = DateTimeOffset.UtcNow;

        return true;
    }

    /// <summary>
    /// Stores an uploaded background, at canvas size.
    ///
    /// Two limits, both his: at most SignatureLimits.MaxBackgrounds per member, and
    /// re-encoded to the canvas so what is kept is bounded by 520x160 rather than by
    /// the file. The gallery's ImageProbe decides whether the bytes are an image at
    /// all, on their own magic number rather than on what the upload claimed.
    ///
    /// At the cap it refuses rather than replacing. Silently overwriting the
    /// background somebody is using is worse than telling them to remove one.
    /// </summary>
    public async Task<SignatureOutcome> AddBackgroundAsync(
        Member member, Stream content, long declaredLength, string? name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(content);

        var held = await db.SignatureBackgrounds.CountAsync(b => b.MemberId == member.Id, ct);

        if (held >= SignatureLimits.MaxBackgrounds)
        {
            return SignatureOutcome.Fail(
                $"You have {held} backgrounds, which is the limit. Remove one first.");
        }

        if (declaredLength > Gallery.ImageProbe.MaxBytes)
        {
            return SignatureOutcome.Fail(
                $"That is larger than the {Gallery.ImageProbe.MaxBytes / 1024 / 1024}MB limit.");
        }

        // The declared length is a claim; this is the check that holds.
        if (await CappedRead.AllAsync(content, Gallery.ImageProbe.MaxBytes, ct) is not { } bytes)
        {
            return SignatureOutcome.Fail(
                $"That is larger than the {Gallery.ImageProbe.MaxBytes / 1024 / 1024}MB limit.");
        }

        if (bytes.Length == 0)
        {
            return SignatureOutcome.Fail("That file was empty.");
        }

        // What it is, from its own bytes. The name and the type it claims are ignored.
        if (Gallery.ImageProbe.Probe(bytes) is null)
        {
            return SignatureOutcome.Fail(
                "That is not a PNG, JPEG, GIF or WebP. What matters is what is inside "
                + "the file, not what it is called.");
        }

        if (SignatureCanvas.Fit(bytes) is not { } fitted)
        {
            return SignatureOutcome.Fail("That picture could not be read.");
        }

        db.SignatureBackgrounds.Add(new SignatureBackground
        {
            MemberId = member.Id,
            Bytes = fitted.Bytes,
            ContentType = "image/png",
            Width = fitted.Width,
            Height = fitted.Height,
            Name = Tidy(name, held + 1),
            UploadedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);

        return new SignatureOutcome(true, null, null);
    }

    /// <summary>A member's own background, for the editor to display.</summary>
    public Task<SignatureBackground?> BackgroundAsync(int memberId, int id, CancellationToken ct) =>
        db.SignatureBackgrounds
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id && b.MemberId == memberId, ct);

    /// <summary>A name for the picker, from the filename or a number.</summary>
    private static string Tidy(string? name, int ordinal)
    {
        var text = Path.GetFileNameWithoutExtension((name ?? "").Trim());

        // Plain text for a label, and short. Nothing here is rendered as markup, but
        // a sixty character filename in a picker is not a label either.
        text = new string(text.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();

        return text.Length switch
        {
            0 => $"Background {ordinal}",
            > 40 => text[..40],
            _ => text,
        };
    }

    /// <summary>The bytes to draw under the text, or null for the flat colour.</summary>
    public async Task<byte[]?> BackgroundAsync(
        SignatureDesign design, int memberId, CancellationToken ct) => design.Background switch
    {
        BackgroundKind.Preset => presets.Read(design.BackgroundKey),
        BackgroundKind.Upload => await db.SignatureBackgrounds
            .Where(b => b.MemberId == memberId && b.Id.ToString() == design.BackgroundKey)
            .Select(b => b.Bytes)
            .FirstOrDefaultAsync(ct),
        _ => null,
    };

    /// <summary>
    /// Everything that decides what the picture looks like, as one digest.
    ///
    /// The resolved text rather than the templates, so a level going up moves it and
    /// a character being renamed moves it, while a daily pass over a member who did
    /// nothing does not.
    /// </summary>
    private static string SourceVersionOf(
        SignatureDesign design, Member member, IReadOnlyList<Character> roster, byte[]? background)
    {
        var parts = new StringBuilder();

        parts.Append(design.Background).Append('|')
            .Append(design.BackgroundKey).Append('|')
            .Append(design.Colour).Append('|')
            .Append(background is null ? "" : Digest(background)).Append('|');

        foreach (var element in design.Elements.Take(SignatureLimits.MaxElements))
        {
            parts.Append(element.X).Append(',').Append(element.Y).Append(',')
                .Append(element.Align).Append(',')
                .Append(element.Font).Append(',')
                .Append(element.Size).Append(',')
                .Append(element.Colour).Append(',')
                .Append(element.Outline).Append('|')
                // What it will actually say, not what it was typed as.
                .Append(SignatureTokens.Resolve(
                    element.Template, SignatureData.Subject(member, roster, element.CharacterId)))
                .Append('\n');
        }

        return Digest(Encoding.UTF8.GetBytes(parts.ToString()));
    }

    private async Task<HashSet<int>> OwnedCharacterIdsAsync(Member member, CancellationToken ct) =>
        (await db.Characters
            .Where(c => c.MemberId == member.Id)
            .Select(c => c.Id)
            .ToListAsync(ct))
        .ToHashSet();

    public static string Serialise(SignatureDesign design) =>
        JsonSerializer.Serialize(design, Json);

    /// <summary>Sixteen hex characters, the same shape as a portrait's version.</summary>
    private static string Digest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

    /// <summary>
    /// A slug for the public URL: twelve characters of Crockford-ish base32 from a
    /// cryptographic source.
    ///
    /// No vowels and no look-alikes, so a slug read aloud or copied out of a forum
    /// post cannot become a different one. Not sequential, because a sequential id in
    /// a public URL tells anybody how many members there are.
    /// </summary>
    public static string NewSlug()
    {
        const string alphabet = "0123456789bcdfghjkmnpqrstvwxyz";

        var slug = new char[Data.Signature.SlugLength];
        var bytes = RandomNumberGenerator.GetBytes(slug.Length);

        for (var i = 0; i < slug.Length; i++)
        {
            slug[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(slug);
    }
}
