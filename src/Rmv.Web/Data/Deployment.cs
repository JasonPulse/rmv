namespace Rmv.Web.Data;

/// <summary>
/// One row per application start. This is the site's proof-of-life: it exercises
/// a write and a read against Postgres on every boot, so a broken connection
/// string or an unapplied migration fails loudly at startup instead of silently
/// on the first page a member visits.
/// </summary>
public class Deployment
{
    public int Id { get; set; }

    /// <summary>Git commit the image was built from, or "local" outside CI.</summary>
    public string Version { get; set; } = "local";

    /// <summary>Container or machine hostname, so multi-host deploys are legible.</summary>
    public string Host { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }
}
