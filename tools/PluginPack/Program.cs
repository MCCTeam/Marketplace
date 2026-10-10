using System.Text.Json;
using PluginPack;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using Tomlet;

try
{
    if (args.Length == 2 && args[0] == "validate")
    {
        string[] manifests = Directory.GetFiles(Path.GetFullPath(args[1]), PluginManifest.FileName, SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")).Order(StringComparer.Ordinal).ToArray();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in manifests)
        {
            if (!PluginManifest.TryParse(File.ReadAllText(path), out PluginManifest manifest, out string? error))
                throw new InvalidDataException(path + ": " + error);
            if (!identities.Add(manifest.Id)) throw new InvalidDataException("pack.duplicate-identity:" + manifest.Id);
            string folder = Path.GetDirectoryName(path)!;
            foreach (string topic in manifest.Man)
                if (!File.Exists(Path.Combine(folder, "man", "en", topic + ".md"))
                    && !File.Exists(Path.Combine(folder, "man", topic + ".md")))
                    throw new InvalidDataException("pack.manual-missing:" + manifest.Id + ":" + topic);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { validated = manifests.Length }));
        return 0;
    }
    if (args.Length >= 7 && args[0] == "pack")
    {
        string? entry = null;
        var provided = new List<string>();
        for (int index = 7; index < args.Length; index++)
        {
            if (args[index] == "--entry" && index + 1 < args.Length) entry = args[++index];
            else provided.Add(args[index]);
        }
        PackedPlugin result = PluginPackageBuilder.Pack(args[1], args[2], args[3], args[4], args[5], new Uri(args[6]), provided, entry);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.ArchivePath,
            result.Manifest.Id,
            result.Manifest.Version,
            sha256 = result.Release.Assets[0].Sha256,
            target = result.Manifest.Target,
            kind = result.Manifest.Kind
        }));
        return 0;
    }
    if (args.Length == 3 && args[0] == "catalogue")
    {
        ReleaseCatalogue[] fragments = Directory.GetFiles(Path.GetFullPath(args[1]), "*.catalogue.toml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => ReleaseCatalogue.Parse(File.ReadAllText(path))).ToArray();
        if (fragments.Length == 0) throw new MarketplaceException("pack.catalogue-empty");
        string directory = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(directory);
        var documents = new List<(string Path, string Text)>();
        foreach (var group in fragments.GroupBy(fragment => fragment.Id).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, group.Key + ".toml");
            ReleaseCatalogue? previous = File.Exists(path) ? ReleaseCatalogue.Parse(File.ReadAllText(path)) : null;
            ReleaseCatalogue merged = ReleaseCatalogueBuilder.Publish(previous, ReleaseCatalogueBuilder.Compose(group));
            documents.Add((path, TomletMain.TomlStringFrom(merged)));
        }
        foreach ((string path, string text) in documents) WriteAtomic(path, text);
        Console.WriteLine(JsonSerializer.Serialize(new { catalogues = documents.Count }));
        return 0;
    }
    if (args.Length == 4 && args[0] == "index")
    {
        string catalogueRoot = Path.GetFullPath(args[2]);
        string destination = Path.GetFullPath(args[3]);
        string parent = Path.GetDirectoryName(destination)!;
        var index = new MarketplaceIndex { SchemaVersion = 2, Id = "official", Name = PackStrings.Get("pack.official-name") };
        foreach (string path in Directory.GetFiles(Path.GetFullPath(args[1]), PluginManifest.FileName, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
            if (!PluginManifest.TryParse(File.ReadAllText(path), out PluginManifest manifest, out string? error))
                throw new InvalidDataException(path + ": " + error);
            string catalogue = Path.Combine(catalogueRoot, manifest.Id + ".toml");
            if (!File.Exists(catalogue)) continue;
            if (ReleaseCatalogue.Parse(File.ReadAllText(catalogue)).Id != manifest.Id)
                throw new MarketplaceException("catalogue.identity-mismatch", manifest.Id);
            index.Plugins.Add(new()
            {
                Id = manifest.Id,
                Description = manifest.Meta.Description,
                Tags = manifest.Meta.Tags,
                Releases = Path.GetRelativePath(parent, catalogue).Replace('\\', '/')
            });
        }
        string text = TomletMain.TomlStringFrom(index);
        _ = MarketplaceIndex.Parse(text);
        WriteAtomic(destination, text);
        Console.WriteLine(JsonSerializer.Serialize(new { plugins = index.Plugins.Count }));
        return 0;
    }
    if (args.Length == 2 && args[0] == "verify-assets")
    {
        ReleaseCatalogue catalogue = ReleaseCatalogue.Parse(File.ReadAllText(args[1]));
        using var http = new HttpClient();
        foreach (ReleaseAsset asset in catalogue.Releases.SelectMany(release => release.Assets))
        {
            using Stream stream = await http.GetStreamAsync(asset.Url);
            string digest = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
            if (!digest.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MarketplaceException("installation.hash-mismatch", asset.Url);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { verified = catalogue.Releases.Sum(release => release.Assets.Count) }));
        return 0;
    }
    Console.Error.WriteLine(PackStrings.Get("pack.usage"));
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception is MarketplaceException failure
        ? JsonSerializer.Serialize(new { failure.Code, failure.Values }) : exception.Message);
    return 1;
}

static void WriteAtomic(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
    finally { File.Delete(temporary); }
}
