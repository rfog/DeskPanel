using DeskPanel.Audio.Ai;
using DeskPanel.Core.Configuration;

namespace DeskPanel.Feeds;

/// <summary>Builds the language-model client chosen in Settings › IA, with its key from the secret store.</summary>
public static class LlmSetup
{
    /// <summary>Returns the client and model, or (null, null) after adding what is missing to <paramref name="problems"/>.</summary>
    public static (ILlmClient? Client, string? Model) Create(AiConfig ai, ISecretStore secrets, List<string> problems)
    {
        string? model, secret;
        switch (ai.Provider)
        {
            case LlmClientFactory.Gemini:
                model = ai.GeminiModel;
                secret = secrets.Get(SecretNames.GeminiKey);
                break;
            case LlmClientFactory.Claude:
                model = ai.ClaudeModel;
                secret = secrets.Get(SecretNames.ClaudeKey);
                break;
            default:
                model = ai.LmStudioModel;
                secret = secrets.Get(SecretNames.LmStudioToken);
                break;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            problems.Add("No hay modelo de IA elegido (Configurar › IA › Listar modelos).");
        }

        try
        {
            return (LlmClientFactory.Create(ai.Provider, ai.LmStudioUrl, secret), model);
        }
        catch (LlmException ex)
        {
            problems.Add(ex.Message + " (Configurar › IA)");
            return (null, null);
        }
    }
}
