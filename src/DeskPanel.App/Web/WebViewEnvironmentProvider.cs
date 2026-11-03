using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace DeskPanel.App.Web;

/// <summary>
/// One shared environment for every WebView2 in the app. The default user-data folder sits next to the exe
/// (often read-only), and separate environments would each spawn their own browser process tree.
/// Must be first awaited on the UI thread.
/// </summary>
internal sealed class WebViewEnvironmentProvider
{
    private readonly string _userDataFolder;
    private Task<CoreWebView2Environment>? _environment;

    public WebViewEnvironmentProvider(string userDataFolder) => _userDataFolder = userDataFolder;

    public Task<CoreWebView2Environment> GetAsync() =>
        _environment ??= CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _userDataFolder);
}
