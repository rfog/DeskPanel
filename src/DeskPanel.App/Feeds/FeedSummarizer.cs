using System;
using System.Threading;
using System.Threading.Tasks;
using DeskPanel.App.Infrastructure;
using DeskPanel.Feeds;

namespace DeskPanel.App.Feeds;

/// <summary>Binds the shared summariser (DeskPanel.Feeds) to this app's configuration and DPAPI secret store.</summary>
internal static class FeedSummarizer
{
    public static Task<FeedSummaryOutcome> GenerateAsync(
        Shell shell, FeedEntry entry, Func<string>? plainTextSource, CancellationToken ct, Action<string>? progress = null) =>
        global::DeskPanel.Feeds.FeedSummarizer.GenerateAsync(shell.Config.App, SecretStoreAdapter.Instance, entry, plainTextSource, ct, progress);

    public static string Save(Shell shell, FeedEntry entry, string markdown) =>
        global::DeskPanel.Feeds.FeedSummarizer.Save(shell.Config.App.Feeds, shell.Config.Paths, shell.ResolveCacheFolder(), entry, markdown);
}
