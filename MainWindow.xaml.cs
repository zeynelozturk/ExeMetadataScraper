using KeyboardSite.Entities.Entities;
using KeyboardSite.FileMetaDataProcessor;
using KeyboardSite.FileMetaDataProcessor.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
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
    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

        // New: prevent re-entrancy and show progress
        private bool _isSending;

        private readonly ObservableCollection<PendingItem> _pending = new();
        public ObservableCollection<PendingItem> Pending => _pending;

        // Expose boolean for XAML instead of binding expression Pending.Count > 0
        public bool HasPending => _pending.Count > 0;

        private static bool IsExePath(string path) =>
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        // ----------------------------------------------------------------------------------
        // Init
        // ----------------------------------------------------------------------------------
        public MainWindow()
        {
            InitializeComponent();
            AppendVersionToTitle();

            _uiDispatcher = DispatcherQueue.GetForCurrentThread();

            _authService = new AuthService();
            _tokenStorage = new TokenStorage();

            // Update bindings whenever collection changes so HasPending re-evaluates
            _pending.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(HasPending));
                UpdateSendButtonState();
            };

            _ = InitializeAuthStateAsync();
            UpdateSendButtonState();
            ConfigureWindow();
        }

        private void ConfigureWindow()
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow?.Resize(new SizeInt32(1080, 820)); // widened window
            if (appWindow?.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = true;
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
        // In UpdateSendButtonState(): replace entire method body with simplified version
        private void UpdateSendButtonState()
        {
            BusyOverlay.Visibility = _isSending ? Visibility.Visible : Visibility.Collapsed;

            if (_isSending)
            {
                SendButton.IsEnabled = false;
                SendButton.Content = "Sending...";
                StatusText.Text = "Uploading...";
                return;
            }

            bool hasFiles = _pending.Count > 0;
            bool canSend = _isAuthenticated && hasFiles;
            SendButton.IsEnabled = canSend;
            SendButton.Content = _pending.Count switch
            {
                0 => "Send",
                1 => "Send 1 item",
                _ => $"Send {_pending.Count} items"
            };

            if (canSend)
            {
                StatusText.Text = "Ready to send";
            }
            else if (!_isAuthenticated && !hasFiles)
            {
                StatusText.Text = "Login and add files";
            }
            else if (!_isAuthenticated)
            {
                StatusText.Text = "Login required";
            }
            else if (!hasFiles)
            {
                StatusText.Text = "Add files to send";
            }
            else
            {
                StatusText.Text = string.Empty;
            }
        }

        private void ClearFileState()
        {
            FileNameTextBlock.Text = "";
            FilePathValueTextBlock.Text = "";
            MetadataGrid.Children.Clear();
            MetadataGrid.RowDefinitions.Clear();
            FileIconImage.Source = null;

            _pending.Clear(); // clear multi-selection

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
        // Send Metadata
        // ----------------------------------------------------------------------------------
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending) return;

            if (_pending.Count == 0)
            {
                await ShowMessageDialog("No file metadata to send. Please select file(s) first.");
                return;
            }

            _isSending = true;
            UpdateSendButtonState();

            try
            {
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

                string apiUrl = ApiRoutes.GetUploadMetadataBatchUrl();

                // Build JSON array from all pending items
                var payloads = new string[_pending.Count];
                for (int i = 0; i < _pending.Count; i++)
                {
                    var p = _pending[i];
                    payloads[i] = FileMetadataSerializer.Serialize(p.Metadata, p.CustomData);
                }
                string jsonArray = "[" + string.Join(",", payloads) + "]";

                try
                {
                    using var httpClient = new HttpClient();
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using var content = new StringContent(jsonArray, System.Text.Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync(apiUrl, content).ConfigureAwait(true);

                    string resp = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                    if (response.IsSuccessStatusCode)
                    {
                        await ShowMessageDialog("Metadata sent successfully! It is added to your drafts.");
                        ClearFileState();
                        ResetDropZoneVisual();
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
            finally
            {
                _isSending = false;
                UpdateSendButtonState();
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
                string.Join(Environment.NewLine, MetadataGrid.Children.OfType<TextBlock>().Select(tb => tb.Text))
            );

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

            // Constrain to .exe only
            picker.FileTypeFilter.Clear();
            picker.FileTypeFilter.Add(".exe");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                await HandleFilesAsync(files);
            }
        }

        private async void OnFileDrop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var files = new List<StorageFile>();
                foreach (var item in items)
                {
                    if (item is StorageFile f)
                        files.Add(f);
                }

                if (files.Count > 0)
                    await HandleFilesAsync(files);
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

        private async void OpenFolderHyperlink_Click(object sender, RoutedEventArgs e)
        {
            var path = _lastFilePath;
            if (string.IsNullOrWhiteSpace(path))
                path = FilePathValueTextBlock.Text;

            if (string.IsNullOrWhiteSpace(path))
            {
                await ShowMessageDialog("No file has been selected.");
                return;
            }

            if (!File.Exists(path))
            {
                await ShowMessageDialog("The file no longer exists at this path.");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await ShowMessageDialog($"Failed to open folder: {ex.Message}");
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

        private void AppendVersionToTitle()
        {
            var ver = GetAppVersion();
            if (string.IsNullOrWhiteSpace(ver)) return;

            this.Title = string.IsNullOrEmpty(this.Title)
                ? $"v{ver}"
                : $"{this.Title} - v{ver}";
        }

        private static string GetAppVersion()
        {
            // 1) Packaged (MSIX): use manifest version
            try
            {
                var pv = Windows.ApplicationModel.Package.Current.Id.Version;
                return Format(new Version(pv.Major, pv.Minor, pv.Build, pv.Revision));
            }
            catch
            {
                // Unpackaged; fall through
            }

            var asm = Assembly.GetEntryAssembly() ?? typeof(App).Assembly;

            // 2) AssemblyVersion (what you want)
            var asmVer = asm.GetName().Version;
            if (asmVer is not null)
                return Format(asmVer);

            // 3) File/Product version
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(asm.Location);
                if (!string.IsNullOrWhiteSpace(fvi.ProductVersion))
                    return Normalize(fvi.ProductVersion);
                if (!string.IsNullOrWhiteSpace(fvi.FileVersion))
                    return Normalize(fvi.FileVersion);
            }
            catch { }

            // 4) InformationalVersion (strip +commit metadata)
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return Normalize(info);

            return string.Empty;

            static string Normalize(string s)
            {
                var core = s.Split('+')[0]; // drop +commit hash if present
                return Version.TryParse(core, out var v) ? Format(v) : core;
            }

            static string Format(Version v)
            {
                // Drop trailing .0 parts for cleaner display
                return v.Revision > 0 ? v.ToString()
                     : v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}"
                                      : $"{v.Major}.{v.Minor}";
            }
        }

        private async Task HandleFilesAsync(IReadOnlyList<StorageFile> files)
        {
            int added = 0;
            foreach (var file in files)
            {
                string filePath = file.Path;

                // Skip URL files
                if (file.FileType.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Resolve .lnk shortcuts (only valid for drag/drop; picker won't allow .lnk)
                if (file.FileType.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = ResolveShortcut(filePath) ?? "";
                    if (string.IsNullOrEmpty(resolved) || !IsExePath(resolved))
                        continue;
                    if (await TryAddFileByPathAsync(resolved))
                        added++;
                    continue;
                }

                if (!IsExePath(filePath))
                    continue;

                if (await TryAddFileByPathAsync(filePath))
                    added++;
            }

            if (added == 0)
            {
                await ShowMessageDialog("No valid .exe files selected.");
            }

            await UpdateSelectionUiAsync();
            UpdateSendButtonState();
        }

        private async Task<bool> TryAddFileByPathAsync(string filePath)
        {
            try
            {
                if (InstallerDetector.IsInstaller(filePath))
                {
                    var fileName = System.IO.Path.GetFileName(filePath) ?? filePath;
                    await ShowMessageDialog($"\"{fileName}\" appears to be an installer. Installer metadata may be declined.");
                }

                var metadata = ExeFileMetaDataHelper.GetMetadata(filePath);
                var iconData = ExeFileMetaDataHelper.ExtractExeIconData(filePath);
                Microsoft.UI.Xaml.Media.ImageSource? iconSource = null;
                var firstIconPng = iconData?.OrderBy(i => i.Width * i.Height).FirstOrDefault()?.PngData;
                if (firstIconPng != null && firstIconPng.Length > 0)
                {
                    using var ms = new MemoryStream(firstIconPng);
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                    iconSource = bitmap;
                }

                var item = new PendingItem
                {
                    Metadata = metadata,
                    CustomData = new CustomFileData { ExeIconDataList = iconData ?? new List<ExeIconData>() },
                    FilePath = filePath,
                    IconSource = iconSource
                };

                _pending.Add(item);
                return true;
            }
            catch (Exception ex)
            {
                var fileName = System.IO.Path.GetFileName(filePath) ?? filePath;
                await ShowMessageDialog($"Failed to read file metadata for \"{fileName}\": {ex.Message}");
                return false;
            }
        }

        private async Task UpdateSelectionUiAsync()
        {
            if (_pending.Count == 0)
            {
                ClearFileState(); // already resets UI
                return;
            }

            // Show first item or keep previously selected visible
            var first = _pending[0];
            await ShowMetadataForItemAsync(first);
        }

        private async Task ShowMetadataForItemAsync(PendingItem item)
        {
            if (item?.Metadata == null)
            {
                FileNameTextBlock.Text = "";
                FilePathValueTextBlock.Text = "";
                MetadataGrid.Children.Clear();
                MetadataGrid.RowDefinitions.Clear();
                FileIconImage.Source = null;
                _lastFilePath = null;
                //SelectedHeader.Visibility = Visibility.Collapsed;
                return;
            }

            //SelectedHeader.Visibility = Visibility.Visible;
            FileNameTextBlock.Text = item.FileName;
            FilePathValueTextBlock.Text = item.FilePath;
            _lastFilePath = item.FilePath;
            if (item.IconSource != null) FileIconImage.Source = item.IconSource;

            var m = item.Metadata;
            RenderMetadataGrid(m);

            if (!string.IsNullOrEmpty(item.FilePath) && item.IconSource == null)
                await UpdateFileIconAsync(item.FilePath);
        }

        private void RenderMetadataGrid(ProgramExeMetaData m)
        {
            MetadataGrid.Children.Clear();
            MetadataGrid.RowDefinitions.Clear();

            void AddRow(string label, string? value)
            {
                int r = MetadataGrid.RowDefinitions.Count;
                MetadataGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });

                var lbl = new TextBlock { Text = label, Style = (Style)RootGrid.Resources["MetaLabel"] };
                var val = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                    Style = (Style)RootGrid.Resources["MetaValue"],
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ToolTipService.SetToolTip(val, value);
                }
                Grid.SetRow(lbl, r);
                Grid.SetRow(val, r);
                Grid.SetColumn(val, 1);

                if (MetadataGrid.ColumnDefinitions.Count == 0)
                {
                    MetadataGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
                    MetadataGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
                }

                MetadataGrid.Children.Add(lbl);
                MetadataGrid.Children.Add(val);
            }

            AddRow("Product Name", m.ProductName);
            AddRow("File Version", m.FileVersion);
            AddRow("Product Version", m.ProductVersion);
            AddRow("Description", m.FileDescription);
            AddRow("Company", m.CompanyName);
            AddRow("Original Name", m.OriginalFileName);
            AddRow("Internal Name", m.InternalName);
            if (!string.IsNullOrWhiteSpace(m.LegalCopyright))
                AddRow("Copyright", m.LegalCopyright);
        }

        // New: remove single pending item (bound to per-row X button)
        private void RemovePending_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                var item = _pending.FirstOrDefault(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                    _pending.Remove(item);

                _ = UpdateSelectionUiAsync();
                UpdateSendButtonState();
            }
        }

        // New: clear all pending
        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            _pending.Clear();
            ClearFileState();
            UpdateSendButtonState();
        }

        // New: clicking an item in list shows its metadata
        private async void SelectedFilesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is PendingItem pi)
                await ShowMetadataForItemAsync(pi);
        }

        // New: double-tap to show metadata details in dialog
        private async void SelectedFilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement fe && fe.DataContext is PendingItem pi)
            {
                await ShowDetailsDialogAsync(pi);
            }
        }

        private async Task ShowDetailsDialogAsync(PendingItem item)
        {
            if (item.Metadata == null) return;

            var sb = new System.Text.StringBuilder();
            var m = item.Metadata;
            void Line(string label, string? val)
            {
                if (!string.IsNullOrWhiteSpace(val)) sb.AppendLine(label + ": " + val);
            }
            Line("File Name", item.FileName);
            Line("Path", item.FilePath);
            Line("Product Name", m.ProductName);
            Line("File Version", m.FileVersion);
            Line("Product Version", m.ProductVersion);
            Line("Description", m.FileDescription);
            Line("Company", m.CompanyName);
            Line("Original Name", m.OriginalFileName);
            Line("Internal Name", m.InternalName);
            Line("Architecture", m.Architecture);
            if (m.IsDigitallySigned.HasValue) Line("Digitally Signed", m.IsDigitallySigned.Value ? "Yes" : "No");
            if (m.SignatureInfo?.Issuer != null) Line("Issuer", m.SignatureInfo.Issuer);
            if (m.SignatureInfo?.ValidTo != null) Line("Valid To", m.SignatureInfo.ValidTo?.ToString("u"));

            var dialog = new ContentDialog
            {
                Title = item.FileName,
                Content = new ScrollViewer { Content = new TextBlock { Text = sb.ToString(), FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                CloseButtonText = "Close",
                XamlRoot = this.Content.XamlRoot,
                PrimaryButtonText = "Copy",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += (_, __) =>
            {
                var dp = new DataPackage();
                dp.SetText(sb.ToString());
                Clipboard.SetContent(dp);
            };
            await dialog.ShowAsync();
        }

        public string FormatItemCount(int count) => $"{count} item(s)";

    }
}