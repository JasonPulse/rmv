namespace Rmv.Web.Configuration;

public static class SecretsDirectory
{
    /// <summary>
    /// Loads one config value per file from a secrets directory, the way Docker
    /// and Kubernetes deliver them. A file named ConnectionStrings__Postgres
    /// becomes the key ConnectionStrings:Postgres.
    ///
    /// Replaces AddKeyPerFile for one reason: <b>empty files are ignored</b>.
    /// AddKeyPerFile treats a 0-byte secret as an empty value, and because the
    /// provider is registered last it wins, so an empty key in a Kubernetes
    /// Secret silently masks a perfectly good environment variable. That looked
    /// exactly like "the feature is switched off" with nothing in the logs.
    ///
    /// A non-empty file still wins over an environment variable, which is the
    /// point of mounting secrets in the first place.
    /// </summary>
    public static IConfigurationBuilder AddSecretsDirectory(
        this IConfigurationBuilder builder, string path)
    {
        if (!Directory.Exists(path))
        {
            return builder;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(path))
        {
            var name = Path.GetFileName(file);

            // Kubernetes atomic-update plumbing: ..data is a symlink to a
            // timestamped directory, and both start with a dot.
            if (name.StartsWith('.'))
            {
                continue;
            }

            // Deliberately not caught: an unreadable secret is a deployment
            // error, and failing loudly is how the missing fsGroup was found.
            // Silently skipping it would present as "no database configured".
            var content = File.ReadAllText(file);

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            // Trailing newlines are common when a secret is written by hand.
            values[name.Replace("__", ":")] = content.Trim();
        }

        return values.Count == 0 ? builder : builder.AddInMemoryCollection(values);
    }
}
