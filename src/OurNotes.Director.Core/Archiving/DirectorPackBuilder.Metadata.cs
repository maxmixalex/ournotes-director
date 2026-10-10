using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static OurNotes.Director.Core.Archiving.ArchiveIndexStorage;

namespace OurNotes.Director.Core.Archiving;

public static partial class DirectorPackBuilder
{
    private static async Task<(JsonNode? Band, string Asset, string Title)> PackIdentityAsync(
        string archiveDirectory, string site, CancellationToken cancellationToken)
    {
        var path = Path.Combine(archiveDirectory, "discovery.json");
        var discovery = File.Exists(path) ? await ReadObjectAsync(path, cancellationToken).ConfigureAwait(false) : new JsonObject();
        var band = discovery["regions"]?[site]?["band"] ?? discovery["band"];
        var textId = Text(band?["bandTextId"] ?? band?["textId"]);
        var token = Text(band?["token"]);
        if (token.Length == 0)
        {
            const string prefix = "Band_Name_";
            token = textId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? textId[prefix.Length..] : textId;
        }
        var name = Text(band?["name"]);
        if (name.Length == 0 || name.Equals("millsage", StringComparison.OrdinalIgnoreCase))
            name = token.Equals("millsage", StringComparison.OrdinalIgnoreCase) ? "Millsage" : token;
        if (name.Length == 0) name = "OurNotes";
        var assetToken = Regex.Replace(token.ToLowerInvariant(), "[^a-z0-9_-]+", "_").Trim('_');
        if (assetToken.Length == 0) assetToken = Text(band?["bandId"]);
        if (assetToken.Length == 0) assetToken = "archive";
        return (Clone(band), "adv_director_" + assetToken, name + " ADV Director Pack");
    }
}
