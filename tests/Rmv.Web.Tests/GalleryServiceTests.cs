using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Rmv.Web.Data;
using Rmv.Web.Gallery;

namespace Rmv.Web.Tests;

/// <summary>
/// Storing and removing screenshots, against a real Postgres because the cascade
/// from a screenshot to its bytes is part of what is being tested.
///
/// The uploads are the real fixture files, so "accepts a PNG" means a PNG an
/// encoder produced rather than eight bytes I typed.
/// </summary>
public class GalleryServiceTests : HeraldDatabaseTests
{
    private GalleryService _gallery = null!;
    private Member _other = null!;

    protected override void ConfigureHerald(FakeHeraldAdapter herald) { }

    protected override async Task SeedAsync()
    {
        _gallery = new GalleryService(Db, NullLogger<GalleryService>.Instance);

        _other = await NewMemberAsync();
    }

    private static byte[] Image(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", name));

    private Task<UploadOutcome> UploadAsync(
        byte[] bytes, string? caption = null, int? gameId = null, Member? who = null) =>
        _gallery.AddAsync(who ?? Member, new MemoryStream(bytes), bytes.Length, caption, gameId, default);

    [Theory]
    [InlineData("shot.png", "image/png")]
    [InlineData("shot.jpg", "image/jpeg")]
    [InlineData("shot.gif", "image/gif")]
    [InlineData("shot.webp", "image/webp")]
    public async Task Stores_a_real_image_with_the_type_read_from_its_bytes(string file, string type)
    {
        var bytes = Image(file);

        var outcome = await UploadAsync(bytes, "Emain, moments before it went wrong");

        Assert.True(outcome.Ok, outcome.Error);
        var shot = outcome.Screenshot!;
        Assert.Equal(type, shot.ContentType);
        Assert.Equal(bytes.Length, shot.Bytes);
        Assert.True(shot.Width > 0 && shot.Height > 0);
        Assert.Equal("Emain, moments before it went wrong", shot.Caption);
        Assert.Equal($"/gallery/{shot.Id}/image", shot.Path);

        var stored = await Db.ScreenshotImages.AsNoTracking()
            .Where(i => i.ScreenshotId == shot.Id)
            .Select(i => i.Bytes)
            .FirstAsync();

        Assert.Equal(bytes, stored);
    }

    [Theory]
    // The one that matters. The endpoint echoes the stored content type, so a file
    // talking its way in as an image while containing markup would be stored
    // cross-site scripting served from our own origin.
    [InlineData("<!DOCTYPE html><script>alert(1)</script>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("not an image at all")]
    public async Task Refuses_anything_that_is_not_one_of_the_four_formats(string content)
    {
        var outcome = await UploadAsync(System.Text.Encoding.UTF8.GetBytes(content));

        Assert.False(outcome.Ok);
        Assert.Contains("PNG, JPEG, GIF or WebP", outcome.Error);
        Assert.False(await Db.Screenshots.AnyAsync(s => s.MemberId == Member.Id));
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var outcome = await UploadAsync([]);

        Assert.False(outcome.Ok);
        Assert.Contains("empty", outcome.Error);
    }

    [Fact]
    public async Task A_lying_length_does_not_get_past_the_cap()
    {
        // The declared length is a claim. This one says the file is small and then
        // streams more than the cap, which is the check that has to hold.
        var bytes = new byte[ImageProbe.MaxBytes + 1024];
        Image("shot.png").CopyTo(bytes, 0);

        var outcome = await _gallery.AddAsync(
            Member, new MemoryStream(bytes), declaredLength: 1024, null, null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("limit is", outcome.Error);
        Assert.False(await Db.Screenshots.AnyAsync(s => s.MemberId == Member.Id));
    }

    [Fact]
    public async Task An_honestly_oversized_file_is_refused_before_it_is_read()
    {
        var outcome = await _gallery.AddAsync(
            Member, new MemoryStream([1, 2, 3]), ImageProbe.MaxBytes + 1, null, null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("limit is", outcome.Error);
    }

    [Fact]
    public async Task A_caption_past_the_limit_is_truncated_rather_than_refused()
    {
        var outcome = await UploadAsync(Image("shot.png"), new string('x', GalleryLimits.MaxCaption + 50));

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(GalleryLimits.MaxCaption, outcome.Screenshot!.Caption.Length);
    }

    [Fact]
    public async Task A_game_that_does_not_exist_is_dropped_rather_than_failing_the_upload()
    {
        var outcome = await UploadAsync(Image("shot.png"), gameId: 999_999);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Null(outcome.Screenshot!.GamePresenceId);
    }

    [Fact]
    public async Task A_real_game_is_kept()
    {
        var outcome = await UploadAsync(Image("shot.png"), gameId: HeraldGameId);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(HeraldGameId, outcome.Screenshot!.GamePresenceId);
    }

    [Fact]
    public async Task The_per_member_cap_is_counted_on_the_server()
    {
        for (var i = 0; i < GalleryLimits.MaxPerMember; i++)
        {
            Db.Screenshots.Add(NewScreenshot(Member.Id, $"filler {i}"));
        }

        await Db.SaveChangesAsync();

        var outcome = await UploadAsync(Image("shot.png"));

        Assert.False(outcome.Ok);
        Assert.Contains("limit", outcome.Error);
        Assert.Equal(GalleryLimits.MaxPerMember,
            await Db.Screenshots.CountAsync(s => s.MemberId == Member.Id));
    }

    [Fact]
    public async Task Removing_your_own_takes_the_bytes_with_it()
    {
        var outcome = await UploadAsync(Image("shot.png"));
        var id = outcome.Screenshot!.Id;

        Assert.True(await _gallery.RemoveAsync(Member, id, mayRemoveAny: false, default));

        Assert.False(await Db.Screenshots.AnyAsync(s => s.Id == id));
        // By cascade, not by remembering to delete it in the handler.
        Assert.False(await Db.ScreenshotImages.AnyAsync(i => i.ScreenshotId == id));
    }

    [Fact]
    public async Task One_member_cannot_remove_another_members_screenshot()
    {
        // Also the blocked-admin case, now that the service takes the admin answer
        // rather than reading the row: whatever the reason the caller was not
        // granted it, what arrives here is false.
        var outcome = await UploadAsync(Image("shot.png"), who: _other);
        var id = outcome.Screenshot!.Id;

        Assert.False(await _gallery.RemoveAsync(Member, id, mayRemoveAny: false, default));
        Assert.True(await Db.Screenshots.AnyAsync(s => s.Id == id));
    }

    [Fact]
    public async Task An_admin_can_remove_anyones()
    {
        // The admin answer is handed in by the caller, which asks the authorization
        // policy for it. The service does not look at the row, because that was a
        // second implementation of a question the policy already answers.
        var outcome = await UploadAsync(Image("shot.png"), who: _other);
        var id = outcome.Screenshot!.Id;

        Assert.True(await _gallery.RemoveAsync(Member, id, mayRemoveAny: true, default));
        Assert.False(await Db.Screenshots.AnyAsync(s => s.Id == id));
    }

    [Fact]
    public async Task Removing_something_that_is_not_there_is_false_rather_than_a_throw()
    {
        Assert.False(await _gallery.RemoveAsync(Member, 999_999, mayRemoveAny: false, default));
    }
}
