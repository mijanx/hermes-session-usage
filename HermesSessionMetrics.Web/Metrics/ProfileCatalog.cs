namespace HermesSessionMetrics.Web.Metrics;

public sealed class ProfileCatalog(string hermesRoot)
{
    private readonly string _hermesRoot = Path.GetFullPath(hermesRoot);

    public IReadOnlyList<ProfileDatabase> Discover()
    {
        var profiles = new List<ProfileDatabase>();
        AddIfLive(profiles, "default", Path.Combine(_hermesRoot, "state.db"));

        var profilesRoot = Path.Combine(_hermesRoot, "profiles");
        if (Directory.Exists(profilesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(profilesRoot))
            {
                var name = Path.GetFileName(directory);
                if (name.Contains("quarantine", StringComparison.OrdinalIgnoreCase)) continue;
                AddIfLive(profiles, name, Path.Combine(directory, "state.db"));
            }
        }

        return profiles
            .OrderBy(x => x.Name.Equals("default", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddIfLive(List<ProfileDatabase> profiles, string name, string path)
    {
        if (File.Exists(path)) profiles.Add(new ProfileDatabase(name, Path.GetFullPath(path)));
    }
}
