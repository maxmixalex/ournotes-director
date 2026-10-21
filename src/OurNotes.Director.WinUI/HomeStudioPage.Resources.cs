using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class HomeStudioPage
{
    internal async Task OpenSourceAsync(long spotId, string site)
    {
        if (!await ConfirmReplaceAsync()) return;
        var data = await Task.Run(() => ReadMetadata(ArchivePath, new(spotId, site), window.Settings.Language), lifetime.Token);
        SetDocument(new(new HomeProject(data.Source) { Duration = Math.Max(1d / 30, data.Duration) }), data);
        await ReloadPreviewAsync();
    }
}
