using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace AguiGroupChat.Desktop;

public partial class MainWindow : Window
{
    private readonly string _baseUrl;

    public MainWindow(string baseUrl)
    {
        InitializeComponent();
        _baseUrl = baseUrl;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            StatusText.Text = L10n.StatusLocalService(_baseUrl);
            WebView.Source = new Uri(_baseUrl);
        }
        catch (Exception ex)
        {
            StatusText.Text = L10n.WebviewInitFailed(ex.Message);
            MessageBox.Show(this,
                L10n.WebviewRuntimeBody(ex.Message), L10n.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnHomeClicked(object sender, RoutedEventArgs e)
        => NavigateTo(_baseUrl);

    private async void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 is not null)
        {
            // 强制刷新：清 HTTP 缓存后重载（保留登录态——不动 sessionStorage / Cookie）
            try
            {
                await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.CacheStorage);
            }
            catch { /* 清缓存失败不影响重载 */ }
            WebView.CoreWebView2.Reload();
            StatusText.Text = L10n.StatusWebviewReloaded(_baseUrl);
        }
        else
        {
            NavigateTo(_baseUrl);
        }
    }

    private void NavigateTo(string url)
    {
        if (WebView.CoreWebView2 is not null)
            WebView.CoreWebView2.Navigate(url);
        else
            WebView.Source = new Uri(url);
    }
}
