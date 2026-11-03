using System.Collections.Generic;
using DeskPanel.Audio.Ai;
using DeskPanel.Core.Configuration;

namespace DeskPanel.App.Infrastructure;

/// <summary>Builds the language-model client chosen in Settings › IA, reading its key from the DPAPI secret store.</summary>
internal static class LlmSetup
{
    public static (ILlmClient? Client, string? Model) Create(AiConfig ai, List<string> problems) =>
        global::DeskPanel.Feeds.LlmSetup.Create(ai, SecretStoreAdapter.Instance, problems);
}
