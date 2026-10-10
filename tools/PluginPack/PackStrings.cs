using System.Globalization;
using System.Resources;

namespace PluginPack;

internal static class PackStrings
{
    private static readonly ResourceManager Resources = new("PluginPack.PackStrings", typeof(PackStrings).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
