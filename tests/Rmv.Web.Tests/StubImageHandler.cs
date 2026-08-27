using System.Net;
using System.Net.Http.Headers;

namespace Rmv.Web.Tests;

/// <summary>
/// Stands in for a herald's portrait route, so the storing and versioning can be
/// tested without asking anyone's server for the same picture repeatedly.
///
/// Counts requests, because "does not download again when the version is
/// unchanged" is the property that makes a daily refresh across every character
/// polite, and a count is the only way to assert it.
/// </summary>
public sealed class StubImageHandler : HttpMessageHandler
{
    /// <summary>A one pixel PNG. Real bytes with a real magic number.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    public int Calls { get; private set; }

    public List<string> Requested { get; } = [];

    /// <summary>Set to make every fetch fail, standing in for a renderer being down.</summary>
    public HttpStatusCode? ForcedStatus { get; set; }

    /// <summary>Set to answer with something we refuse to store.</summary>
    public string ContentType { get; set; } = "image/png";

    public byte[] Body { get; set; } = Png;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Requested.Add(request.RequestUri!.ToString());

        if (ForcedStatus is { } status)
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        var content = new ByteArrayContent(Body);
        content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
