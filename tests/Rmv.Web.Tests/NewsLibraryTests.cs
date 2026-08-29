using Rmv.Web.Content;

namespace Rmv.Web.Tests;

/// <summary>
/// Front matter parsing and slugs.
///
/// A post is a file an operator drops into a mounted directory, so every way of
/// getting one wrong has to be a post that does not appear rather than a page that
/// breaks. That is the property most of these pin.
/// </summary>
public class NewsLibraryTests
{
    private const string Good = """
        ---
        title: The site has news now
        date: 2026-08-28
        author: Jason
        ---

        First paragraph, which is the excerpt.

        Second paragraph, which is not.
        """;

    [Fact]
    public void Reads_the_front_matter_and_renders_the_body()
    {
        var post = NewsLibrary.Parse("2026-08-28-the-site-has-news-now", Good);

        Assert.NotNull(post);
        Assert.Equal("the-site-has-news-now", post.Slug);
        Assert.Equal("The site has news now", post.Title);
        Assert.Equal(new DateOnly(2026, 8, 28), post.Date);
        Assert.Equal("Jason", post.Author);
        Assert.Contains("<p>First paragraph", post.Html);
        Assert.Contains("Second paragraph", post.Html);
    }

    [Fact]
    public void The_excerpt_is_the_first_paragraph_as_plain_text()
    {
        var post = NewsLibrary.Parse("2026-08-28-x", Good);

        Assert.Equal("First paragraph, which is the excerpt.", post!.Excerpt);
        Assert.DoesNotContain("Second paragraph", post.Excerpt);
        Assert.DoesNotContain("<", post.Excerpt);
    }

    [Fact]
    public void An_author_is_optional()
    {
        var post = NewsLibrary.Parse("2026-08-28-x", """
            ---
            title: No byline
            date: 2026-08-28
            ---

            Body.
            """);

        Assert.NotNull(post);
        Assert.Null(post.Author);
    }

    [Theory]
    // No front matter at all.
    [InlineData("Just a body with no front matter.")]
    // Opened and never closed.
    [InlineData("---\ntitle: Unclosed\ndate: 2026-08-28\n\nBody.")]
    // A title is the one thing a listing cannot invent.
    [InlineData("---\ndate: 2026-08-28\n---\n\nBody.")]
    [InlineData("---\ntitle:\ndate: 2026-08-28\n---\n\nBody.")]
    // So is a date, since it decides the order.
    [InlineData("---\ntitle: No date\n---\n\nBody.")]
    [InlineData("---\ntitle: Bad date\ndate: last tuesday\n---\n\nBody.")]
    public void A_post_that_cannot_be_read_is_skipped_rather_than_guessed_at(string text)
    {
        Assert.Null(NewsLibrary.Parse("2026-08-28-x", text));
    }

    [Theory]
    [InlineData("2026-08-28-the-site-has-news-now", "the-site-has-news-now")]
    [InlineData("2001-01-01-a", "a")]
    [InlineData("2026-08-28-Mixed_Case-Slug", "mixed_case-slug")]
    public void The_slug_is_the_filename_after_the_date(string fileName, string expected)
    {
        Assert.Equal(expected, NewsLibrary.SlugFrom(fileName));
    }

    [Theory]
    // No date prefix.
    [InlineData("some-slug")]
    // Date but no slug.
    [InlineData("2026-08-28")]
    [InlineData("2026-08-28-")]
    // Not a date.
    [InlineData("not-a-date-slug")]
    [InlineData("20260828-slug")]
    // A slug is a URL, so anything that is not safe in one is refused rather than
    // escaped. Path traversal is included in that by construction.
    [InlineData("2026-08-28-../../etc/passwd")]
    [InlineData("2026-08-28-with spaces")]
    [InlineData("2026-08-28-with/slash")]
    [InlineData("2026-08-28-percent%20")]
    public void A_filename_that_is_not_the_convention_has_no_slug(string fileName)
    {
        Assert.Null(NewsLibrary.SlugFrom(fileName));
    }

    [Fact]
    public void Raw_html_in_a_post_is_text_rather_than_markup()
    {
        // The files arrive by volume mount. Switching the renderer's HTML off costs
        // one call and rules out a script tag reaching a reader.
        var post = NewsLibrary.Parse("2026-08-28-x", """
            ---
            title: Nice try
            date: 2026-08-28
            ---

            <script>alert(1)</script>

            <iframe src="https://example.com"></iframe>
            """);

        Assert.NotNull(post);
        Assert.DoesNotContain("<script", post.Html);
        Assert.DoesNotContain("<iframe", post.Html);
        Assert.Contains("&lt;script", post.Html);
    }

    [Fact]
    public void Markdown_links_and_lists_still_render()
    {
        var post = NewsLibrary.Parse("2026-08-28-x", """
            ---
            title: Formatting
            date: 2026-08-28
            ---

            A [link](https://example.com) and `code`.

            - one
            - two
            """);

        Assert.NotNull(post);
        Assert.Contains("<a href=\"https://example.com\"", post.Html);
        Assert.Contains("<code>code</code>", post.Html);
        Assert.Contains("<li>one</li>", post.Html);
    }

    [Fact]
    public void Windows_line_endings_parse_the_same()
    {
        // A post written on Windows and copied to the mount is the likely case.
        var post = NewsLibrary.Parse("2026-08-28-x", Good.Replace("\n", "\r\n"));

        Assert.NotNull(post);
        Assert.Equal("The site has news now", post.Title);
        Assert.Equal(new DateOnly(2026, 8, 28), post.Date);
    }
}
