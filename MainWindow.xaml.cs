using KeyboardSite.Entities.Entities;
using KeyboardSite.FileMetaDataProcessor;
using KeyboardSite.FileMetaDataProcessor.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Networking.Sockets;
using Windows.Storage;
using WindowsShortcutFactory;
using static KeyboardSite.FileMetaDataProcessor.ExeFileMetaDataHelper;

namespace WinUIMetadataScraper
{
    public sealed partial class MainWindow : Window
    {
        // ----------------------------------------------------------------------------------
        // Constants
        // ----------------------------------------------------------------------------------
        private const string PlaceholderMetadataText = "File information will appear here.";
        private const int LoginWaitTotalMs = 60_000;
        private const int LoginWaitPollMs = 500;

        // ----------------------------------------------------------------------------------
        // Fields
        // ----------------------------------------------------------------------------------
        private readonly AuthService _authService;
        private readonly TokenStorage _tokenStorage;
        private ProgramExeMetaData? _lastMetadata;
        private CustomFileData _lastCustomData = new();
        private string? _lastFilePath;
        private bool _isAuthenticated;
        private int _callbackPort = -1;
        private StreamSocketListener? _listener;
        private readonly DispatcherQueue _uiDispatcher;

        // ----------------------------------------------------------------------------------
        // Init
        // ----------------------------------------------------------------------------------
        public MainWindow()
        {
            InitializeComponent();

            _uiDispatcher = DispatcherQueue.GetForCurrentThread();

            _authService = new AuthService();
            _tokenStorage = new TokenStorage();

            _ = InitializeAuthStateAsync();
            UpdateSendButtonState();
            ConfigureWindow();
        }

        private void ConfigureWindow()
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow?.Resize(new SizeInt32(1050, 1550));
            if (appWindow?.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = false;
                p.IsMaximizable = false;
            }

            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
            appWindow?.SetIcon(iconPath);
        }

        // ----------------------------------------------------------------------------------
        // Authentication
        // ----------------------------------------------------------------------------------
        private async Task InitializeAuthStateAsync()
        {
            try
            {
                var token = _tokenStorage.GetToken();
                _isAuthenticated = false;

                if (!string.IsNullOrWhiteSpace(token))
                {
                    var displayName = await _authService.GetUserDisplayNameAsync(token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        _isAuthenticated = true;
                        RunOnUi(() => AuthStatusTextBlock.Text = $"Authenticated as: {displayName}");
                    }
                    else
                    {
                        _tokenStorage.SaveToken(null);
                        RunOnUi(() => AuthStatusTextBlock.Text = "Not authenticated");
                    }
                }
                else
                {
                    RunOnUi(() => AuthStatusTextBlock.Text = "Not authenticated");
                }
            }
            catch
            {
                _isAuthenticated = false;
                _tokenStorage.SaveToken(null);
                RunOnUi(() => AuthStatusTextBlock.Text = "Not authenticated");
            }
            finally
            {
                RunOnUi(() =>
                {
                    UpdateAuthUi();
                    UpdateSendButtonState();
                });
            }
        }

        private async Task<bool> EnsureAuthenticatedAsync()
        {
            if (_isAuthenticated) return true;

            await ShowMessageDialog("You need to login before sending. Opening login window...");
            LoginButton_Click(LoginButton, null);

            int waited = 0;
            while (waited < LoginWaitTotalMs)
            {
                await Task.Delay(LoginWaitPollMs).ConfigureAwait(false);
                var token = _tokenStorage.GetToken();
                if (!string.IsNullOrEmpty(token))
                {
                    var displayName = await _authService.GetUserDisplayNameAsync(token).ConfigureAwait(false);
                    _isAuthenticated = !string.IsNullOrWhiteSpace(displayName);
                    break;
                }
                waited += LoginWaitPollMs;
            }

            RunOnUi(UpdateSendButtonState);
            return _isAuthenticated;
        }

        private void UpdateAuthUi()
        {
            LoginButton.Visibility = _isAuthenticated ? Visibility.Collapsed : Visibility.Visible;
            LogoutButton.Visibility = _isAuthenticated ? Visibility.Visible : Visibility.Collapsed;
        }

        // ----------------------------------------------------------------------------------
        // UI State
        // ----------------------------------------------------------------------------------
        private void UpdateSendButtonState()
        {
            bool hasFile = _lastMetadata != null && !string.IsNullOrEmpty(_lastFilePath);
            SendButton.IsEnabled = _isAuthenticated && hasFile;

            if (SendButton.IsEnabled)
            {
                SendStatusText.Text = "";
                SendStatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                SendStatusText.Visibility = Visibility.Visible;
                if (!_isAuthenticated && !hasFile)
                    SendStatusText.Text = "Login and select a file to enable";
                else if (!_isAuthenticated)
                    SendStatusText.Text = "Login to enable";
                else
                    SendStatusText.Text = "Select a file to enable";
            }
        }

        private void ClearFileState()
        {
            FileNameTextBlock.Text = "";
            FilePathValueTextBlock.Text = "";
            FileMetadataTextBlock.Text = PlaceholderMetadataText;
            FileIconImage.Source = null;
            _lastMetadata = null;
            _lastCustomData = new CustomFileData();
            _lastFilePath = null;
        }

        private void ResetDropZoneVisual()
        {
            DropZone.Background = GetBrush("DropZoneBackgroundBrush");
            DropZone.BorderBrush = GetBrush("DropZoneBorderBrush");
            DropZone.BorderThickness = new Thickness(2);
        }

        // ----------------------------------------------------------------------------------
        // File Handling
        // ----------------------------------------------------------------------------------
        private async Task HandleFileAsync(StorageFile file)
        {
            string filePath = file.Path;

            if (file.FileType.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                await ShowMessageDialog("The selected file is a .URL file and cannot be processed.");
                ClearFileState();
                UpdateSendButtonState();
                return;
            }

            if (file.FileType.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                filePath = ResolveShortcut(filePath) ?? "";
                if (string.IsNullOrEmpty(filePath))
                {
                    await ShowMessageDialog("The shortcut is invalid or unsupported.");
                    ClearFileState();
                    UpdateSendButtonState();
                    return;
                }
                if (filePath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
                {
                    await ShowMessageDialog("Steam link shortcuts are not supported.");
                    ClearFileState();
                    UpdateSendButtonState();
                    return;
                }
            }

            if (!filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                await ShowMessageDialog("The selected file is not a supported executable (.exe).");
                ClearFileState();
                UpdateSendButtonState();
                return;
            }

            try
            {
                if (InstallerDetector.IsInstaller(filePath))
                {
                    await ShowMessageDialog("This file appears to be an installer. Installer metadata may be declined.");
                }

                var metadata = ExeFileMetaDataHelper.GetMetadata(filePath);
                var iconData = ExeFileMetaDataHelper.ExtractExeIconData(filePath);

                _lastMetadata = metadata;
                _lastCustomData.ExeIconDataList = iconData ?? new List<ExeIconData>();
                _lastFilePath = filePath;

                FileNameTextBlock.Text = System.IO.Path.GetFileName(filePath);
                FilePathValueTextBlock.Text = filePath;
                await UpdateFileIconAsync(filePath);

                FileMetadataTextBlock.Text =
                    $"File Version: {metadata.FileVersion}\n" +
                    $"Product Name: {metadata.ProductName}\n" +
                    $"File Description: {metadata.FileDescription}\n" +
                    $"Company Name: {metadata.CompanyName}\n" +
                    $"Original Filename: {metadata.OriginalFileName}\n" +
                    $"Internal Name: {metadata.InternalName}\n" +
                    $"Product Version: {metadata.ProductVersion}\n" +
                    $"Copyright: {metadata.LegalCopyright}";
            }
            catch (Exception ex)
            {
                await ShowMessageDialog($"Failed to read file metadata: {ex.Message}");
                ClearFileState();
            }

            UpdateSendButtonState();
        }

        // ----------------------------------------------------------------------------------
        // Send Metadata
        // ----------------------------------------------------------------------------------
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastMetadata == null || string.IsNullOrEmpty(_lastFilePath))
            {
                await ShowMessageDialog("No file metadata to send. Please select a file first.");
                return;
            }

            if (!await EnsureAuthenticatedAsync().ConfigureAwait(true))
            {
                await ShowMessageDialog("Authentication failed; cannot send.");
                return;
            }

            string token = _tokenStorage.GetToken();
            if (string.IsNullOrEmpty(token))
            {
                await ShowMessageDialog("No valid token after login attempt.");
                return;
            }

            string apiUrl = ApiRoutes.GetUploadMetadataUrl();
            string json = FileMetadataSerializer.Serialize(_lastMetadata, _lastCustomData);

            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(apiUrl, content).ConfigureAwait(true);

                string resp = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    await ShowMessageDialog("Metadata sent successfully! It is added to your drafts.");
                    ClearFileState();
                    ResetDropZoneVisual();
                    UpdateSendButtonState();
                }
                else
                {
                    await ShowMessageDialog($"Failed to send metadata. Status: {response.StatusCode}\n{resp}");
                }
            }
            catch (Exception ex)
            {
                await ShowMessageDialog($"Error sending metadata: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------------------------
        // Authentication UI Actions
        // ----------------------------------------------------------------------------------
        private async void LoginButton_Click(object sender, RoutedEventArgs? e)
        {
            try
            {
                string loginUrl = $"{ApiRoutes.GetBaseUrl()}/Account/Login";
                if (_callbackPort == -1)
                    _callbackPort = FindAvailablePort(8080);

                string callbackUrl = $"http://localhost:{_callbackPort}";
                var uri = new Uri($"{loginUrl}?returnUrl={Uri.EscapeDataString(callbackUrl)}");
                await Windows.System.Launcher.LaunchUriAsync(uri);

                string token = await ListenForCallbackAsync(callbackUrl).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(token))
                {
                    _tokenStorage.SaveToken(token);
                    var displayName = await _authService.GetUserDisplayNameAsync(token);
                    _isAuthenticated = !string.IsNullOrWhiteSpace(displayName);
                    AuthStatusTextBlock.Text = _isAuthenticated
                        ? $"Authenticated as: {displayName}"
                        : "Not authenticated";
                    UpdateAuthUi();
                    UpdateSendButtonState();
                    BringToForeground();
                }
                else
                {
                    await ShowMessageDialog("Login failed: No token received.");
                }
            }
            catch (Exception ex)
            {
                await ShowMessageDialog($"Login failed: {ex.Message}");
            }
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            _tokenStorage.SaveToken(null);
            _isAuthenticated = false;
            AuthStatusTextBlock.Text = "Not authenticated";
            UpdateAuthUi();
            UpdateSendButtonState();
        }

        // ----------------------------------------------------------------------------------
        // Local Callback Mini HTTP
        // ----------------------------------------------------------------------------------
        private static int FindAvailablePort(int startPort)
        {
            for (int port = startPort; port < 65_535; port++)
            {
                try
                {
                    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                    listener.Start();
                    return port;
                }
                catch { /* continue */ }
            }
            throw new InvalidOperationException("No available ports found.");
        }

        private async Task<string> ListenForCallbackAsync(string callbackUrl)
        {
            var uri = new Uri(callbackUrl);
            string port = uri.Port.ToString();

            _listener?.Dispose();
            _listener = new StreamSocketListener();
            var tokenSource = new TaskCompletionSource<string>();

            _listener.ConnectionReceived += async (_, args) =>
            {
                string token = await HandleConnectionAsync(args).ConfigureAwait(false);
                using (var writer = new Windows.Storage.Streams.DataWriter(args.Socket.OutputStream))
                {
                    string html =
    "<!doctype html><html><head><meta charset='utf-8'>" +
    "<title>Login Successful</title></head><body>" +
    "<script>try{history.replaceState(null,'','/');}catch(e){}</script>" +
    "<h2>Login Successful</h2><p>You can close this window.</p></body></html>";
                    string response =
    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\n\r\n{html}";
                    writer.WriteString(response);
                    await writer.StoreAsync();
                }
                tokenSource.TrySetResult(token);
            };

            await _listener.BindServiceNameAsync(port);
            var finished = await Task.WhenAny(tokenSource.Task, Task.Delay(60_000));
            _listener.Dispose();
            _listener = null;

            return finished == tokenSource.Task ? tokenSource.Task.Result : string.Empty;
        }

        private static async Task<string> HandleConnectionAsync(StreamSocketListenerConnectionReceivedEventArgs args)
        {
            using var reader = new Windows.Storage.Streams.DataReader(args.Socket.InputStream)
            {
                InputStreamOptions = Windows.Storage.Streams.InputStreamOptions.Partial
            };
            await reader.LoadAsync(1024);
            string request = reader.ReadString(reader.UnconsumedBufferLength);
            if (request.StartsWith("GET", StringComparison.Ordinal))
            {
                int qStart = request.IndexOf("?", StringComparison.Ordinal);
                int qEnd = request.IndexOf(" ", qStart, StringComparison.Ordinal);
                if (qStart != -1 && qEnd > qStart)
                {
                    string query = request.Substring(qStart, qEnd - qStart);
                    var queryParams = System.Web.HttpUtility.ParseQueryString(query);
                    return queryParams["token"] ?? string.Empty;
                }
            }
            return string.Empty;
        }

        // ----------------------------------------------------------------------------------
        // Misc UI Handlers
        // ----------------------------------------------------------------------------------
        private void DropZone_DragLeave(object sender, DragEventArgs e) => ResetDropZoneVisual();
        private void DropZone_Tapped(object sender, TappedRoutedEventArgs e) => BrowseButton_Click(sender, e);

        private async void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            string combined = string.Join(
                Environment.NewLine + Environment.NewLine,
                FileNameTextBlock.Text?.Trim(),
                FilePathValueTextBlock.Text?.Trim(),
                FileMetadataTextBlock.Text?.Trim());

            if (string.IsNullOrWhiteSpace(combined))
                combined = "No metadata.";

            var dp = new DataPackage();
            dp.SetText(combined);
            Clipboard.SetContent(dp);
            Clipboard.Flush();

            await ShowMessageDialog("Metadata copied to clipboard.");
        }

        private void BringToForeground()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetForegroundWindow(hwnd);
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ----------------------------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------------------------
        private Brush GetBrush(string key)
        {
            if (RootGrid.Resources.TryGetValue(key, out var local) && local is Brush lb) return lb;
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(key, out var global) &&
                global is Brush gb) return gb;

            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private void RunOnUi(Action action)
        {
            if (_uiDispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                _ = _uiDispatcher.TryEnqueue(() => action());
            }
        }

        private async Task ShowMessageDialog(string message, string title = "Information")
        {
            // Avoid stacking multiple dialogs if user triggers fast
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async Task UpdateFileIconAsync(string filePath)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem);
                if (thumbnail != null)
                {
                    var bitmapImage = new BitmapImage();
                    await bitmapImage.SetSourceAsync(thumbnail);
                    FileIconImage.Source = bitmapImage;
                }
            }
            catch (Exception ex)
            {
                FileIconImage.Source = null;
                System.Diagnostics.Debug.WriteLine($"Error updating file icon: {ex.Message}");
            }
        }

        private string? ResolveShortcut(string lnkPath)
        {
            try
            {
                var shortcut = WindowsShortcut.Load(lnkPath);
                return shortcut.Path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error resolving shortcut: {ex.Message}");
                return null;
            }
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".cmd");
            picker.FileTypeFilter.Add(".msi");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await HandleFileAsync(file);
            }
        }

        private async void OnFileDrop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file)
                    await HandleFileAsync(file);
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;

            if (DropZone != null)
            {
                DropZone.Background = GetBrush("DropZoneDragOverBackgroundBrush");
                DropZone.BorderBrush = GetBrush("DropZoneHighlightBorderBrush");
                DropZone.BorderThickness = new Thickness(2.5);
            }
        }

        private async void OpenFolderHyperlink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
        {
            if (!string.IsNullOrEmpty(_lastFilePath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{_lastFilePath}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    await ShowMessageDialog($"Failed to open folder and select file: {ex.Message}");
                }
            }
            else
            {
                await ShowMessageDialog("No file has been selected.");
            }
        }

        private void DropZone_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
            {
                BrowseButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}