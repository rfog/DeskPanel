using System;
using System.Threading;
using System.Threading.Tasks;
using DeskPanel.App.Infrastructure;
using DeskPanel.Feeds;

namespace DeskPanel.App.Feeds;

/// <summary>Binds the shared translator (DeskPanel.Feeds) to this app's configuration and DPAPI secret store.</summary>
internal static class FeedTranslator
{
    public static Task<FeedTranslationOutcome> TranslateAsync(Shell shell, FeedEntry entry, CancellationToken ct, Action<string>? progress = null) =>
        global::DeskPanel.Feeds.FeedTranslator.TranslateAsync(shell.Config.App, SecretStoreAdapter.Instance, entry, ct, progress);
}
