using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RedstoneOrderNotifier
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    public class AppConfig
    {
        public string ServerUrl { get; set; } = "https://checkstatskingmc-t2c4.onrender.com";
        public int PlayerRecheckMinutes { get; set; } = 10;
        public double RedstoneBlockThreshold { get; set; } = 50000.0;
        public double RedstoneDustThreshold { get; set; } = 5600.0;
        public List<string> PlayersToMonitor { get; set; } = new List<string> { "Tr0ngX", "Tr0ngXXX", "Tr0ngXBot", "LAMDEPZAIK131" };
    }

    public class OrderItem
    {
        public int slot { get; set; }
        public string? itemName { get; set; }
        public string? displayName { get; set; }
        public string? buyer { get; set; }
        public string? quantity { get; set; }
        public string? remaining { get; set; }
        public string? price { get; set; }
        public string? delivered { get; set; }
    }

    public class OrderCategoryResult
    {
        public List<OrderItem>? orders { get; set; }
        public string? serverUsed { get; set; }
        public string? error { get; set; }
    }

    public class RedstoneApiResponse
    {
        public bool success { get; set; }
        public string? timestamp { get; set; }
        public OrderCategoryResult? redstoneBlock { get; set; }
        public OrderCategoryResult? redstoneDust { get; set; }
        public string? error { get; set; }
    }

    public class GenericOrderApiResponse
    {
        public bool success { get; set; }
        public string? item { get; set; }
        public List<OrderItem>? orders { get; set; }
        public string? serverUsed { get; set; }
        public string? error { get; set; }
    }

    public class PlayerCheckResponse
    {
        public bool success { get; set; }
        public string? player { get; set; }
        public string? avatarUrl { get; set; }
        public string? balance { get; set; }
        public bool isOnline { get; set; }
        public string? onlineMessage { get; set; }
        public string? ping { get; set; }
        public string? world { get; set; }
        public string? timestamp { get; set; }
    }

    public class PlayerState
    {
        public string Name { get; set; } = "";
        public string Balance { get; set; } = "Chưa quét";
        public double NumericBalance { get; set; } = 0;
        public bool IsOnline { get; set; } = false;
        public string Ping { get; set; } = "N/A";
        public string World { get; set; } = "N/A";
        public string? AvatarUrl { get; set; }
        public DateTime? LastChecked { get; set; }
        public List<BalancePoint> History { get; set; } = new List<BalancePoint>();
    }

    public class BalancePoint
    {
        public DateTime Time { get; set; }
        public double Value { get; set; }
    }

    public class WebMessage
    {
        public string? action { get; set; }
        public string? name { get; set; }
        public string? value { get; set; }
    }

    public class MainForm : Form
    {
        private readonly HttpClient _httpClient;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly string _configFile;
        private readonly WebView2 _webView;

        private AppConfig _config = new AppConfig();
        private readonly Dictionary<string, PlayerState> _players = new Dictionary<string, PlayerState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<OrderItem>> _latestOrders = new Dictionary<string, List<OrderItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _notifiedOrders = new HashSet<string>();

        // Danh sách Order quét liên tục theo vòng tuần tự khi rảnh (gồm Redstone block/dust, TNT, Sand, Gunpowder, Bone, Bone Block, Kelp, Dried Kelp, Dried Kelp Block)
        private readonly string[] _orderItemList = new[]
        {
            "redstone_pair",
            "tnt",
            "sand",
            "gunpowder",
            "bone",
            "bone_block",
            "kelp",
            "dried_kelp",
            "dried_kelp_block"
        };
        private int _currentOrderItemIndex = 0;

        private System.Windows.Forms.Timer _workerLoopTimer = null!;
        private DateTime _lastPlayerCheckTime = DateTime.MinValue;
        private bool _isBusyTask = false;
        private bool _isRunning = true;
        private string _currentActionText = "Đang khởi tạo...";

        public MainForm()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            _configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            LoadConfig();

            foreach (var p in _config.PlayersToMonitor)
            {
                if (!_players.ContainsKey(p))
                {
                    _players[p] = new PlayerState
                    {
                        Name = p,
                        AvatarUrl = $"https://mc-heads.net/avatar/{Uri.EscapeDataString(p)}/64"
                    };
                }
            }

            Text = "KingMC Supreme Monitor • [2 IP Render Engine] By Tr0ngX";
            Size = new System.Drawing.Size(1200, 800);
            MinimumSize = new System.Drawing.Size(1000, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = System.Drawing.Color.FromArgb(3, 7, 18);
            Icon = System.Drawing.SystemIcons.Shield;

            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Mở giao diện", null, (s, e) => ShowAndRestore());
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Thoát", null, (s, e) => ExitApp());

            _trayIcon = new NotifyIcon
            {
                Text = "KingMC Supreme Monitor",
                Icon = System.Drawing.SystemIcons.Shield,
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => ShowAndRestore();

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            Shown += async (s, e) => await InitializeWebViewAsync();

            _workerLoopTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _workerLoopTimer.Tick += async (s, e) => await MasterCoordinatorTickAsync();

            FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    ShowWindowsToast("KingMC Monitor", "Ứng dụng đang chạy nền dưới khay hệ thống (System Tray).");
                }
            };
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                if (!IsHandleCreated) CreateHandle();
                if (!_webView.IsHandleCreated) _webView.CreateControl();

                // Thư mục dữ liệu độc lập cho WebView2 tránh xung đột cache
                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KingMCMonitor_WebView2");
                
                // Cờ tham số Chromium chuẩn tương thích 100% với WebView2 (Đã gỡ --disable-gpu-compositing để khắc phục triệt để màn hình đen)
                var options = new CoreWebView2EnvironmentOptions(
                    "--allow-file-access-from-files --disable-features=CalculateNativeWinOcclusion,SpareRendererForSitePerProcess --disable-background-timer-throttling --disable-backgrounding-occluded-windows"
                );

                CoreWebView2Environment env;
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                }
                catch
                {
                    // Nếu folderUserData cũ bị khóa hoặc hư hỏng cache do lỗi trước đó, tự động dọn dẹp và khởi tạo lại
                    try { if (Directory.Exists(userDataFolder)) Directory.Delete(userDataFolder, true); } catch { }
                    env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                }

                await _webView.EnsureCoreWebView2Async(env);

                // Thiết lập màu nền mặc định tối cùng màu ứng dụng
                _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(2, 4, 10);

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                _webView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;

                var uiFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UI");
                var uiPath = Path.Combine(uiFolder, "index.html");

                if (Directory.Exists(uiFolder))
                {
                    try
                    {
                        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            "app.kingmc.local",
                            uiFolder,
                            CoreWebView2HostResourceAccessKind.Allow
                        );
                    }
                    catch { }
                }

                if (File.Exists(uiPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(uiPath).AbsoluteUri);
                }
                else
                {
                    _webView.CoreWebView2.NavigateToString("<h2 style='color:white;font-family:sans-serif;padding:20px;'>⚠️ Không tìm thấy tệp giao diện UI/index.html</h2>");
                }

                // Tự động hồi phục khi GPU process hoặc Render process bị sập
                _webView.CoreWebView2.ProcessFailed += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[WebView2 ProcessFailed] {e.ProcessFailedKind} - {e.Reason}");
                    try
                    {
                        _webView.Reload();
                    }
                    catch { }
                };

                _webView.NavigationCompleted += (s, e) =>
                {
                    if (!e.IsSuccess)
                    {
                        AppendLog($"⚠️ Lỗi nạp WebView2 ({e.WebErrorStatus})...");
                    }
                    SyncDataToFrontend();
                    _workerLoopTimer.Start();
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khởi tạo giao diện WebView2: " + ex.Message + "\n\nNếu máy tính chưa có Evergreen WebView2 Runtime, vui lòng cài đặt lại Microsoft Edge WebView2 Runtime.", "Lỗi WebView2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var rawJson = e.WebMessageAsJson;
                var msg = JsonSerializer.Deserialize<WebMessage>(rawJson);
                if (msg == null) return;

                if (msg.action == "add_player" && !string.IsNullOrWhiteSpace(msg.name))
                {
                    var clean = msg.name.Trim();
                    if (!_players.ContainsKey(clean))
                    {
                        _players[clean] = new PlayerState
                        {
                            Name = clean,
                            AvatarUrl = $"https://mc-heads.net/avatar/{Uri.EscapeDataString(clean)}/64"
                        };
                        if (!_config.PlayersToMonitor.Contains(clean, StringComparer.OrdinalIgnoreCase))
                        {
                            _config.PlayersToMonitor.Add(clean);
                            SaveConfig();
                        }
                        SyncDataToFrontend();
                        AppendLog($"➕ Đã thêm người chơi [{clean}] vào danh sách theo dõi.");
                    }
                }
                else if (msg.action == "remove_player" && !string.IsNullOrWhiteSpace(msg.name))
                {
                    var clean = msg.name.Trim();
                    if (_players.Remove(clean))
                    {
                        _config.PlayersToMonitor.RemoveAll(x => x.Equals(clean, StringComparison.OrdinalIgnoreCase));
                        SaveConfig();
                        SyncDataToFrontend();
                        AppendLog($"➖ Đã xóa người chơi [{clean}] khỏi danh sách.");
                    }
                }
                else if (msg.action == "set_server_url" && !string.IsNullOrWhiteSpace(msg.value))
                {
                    _config.ServerUrl = msg.value.Trim().TrimEnd('/');
                    SaveConfig();
                    AppendLog($"🌐 Đã cập nhật Master Render URL: {_config.ServerUrl}");
                }
                else if (msg.action == "recheck_now")
                {
                    _lastPlayerCheckTime = DateTime.MinValue;
                    AppendLog("⚡ Kích hoạt quét toàn bộ người chơi ngay lập tức!");
                    _ = MasterCoordinatorTickAsync();
                }
                else if (msg.action == "test_toast")
                {
                    ShowWindowsToast("Test Toast WinRT", "Hệ thống thông báo Windows built-in đang hoạt động hoàn hảo!\nTác giả: Tr0ngX (t.me/TrongX)");
                    AppendLog("🔔 Đã bắn test toast thông báo Windows.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("⚠️ Lỗi xử lý tin nhắn giao diện: " + ex.Message);
            }
        }

        private void SyncDataToFrontend()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(SyncDataToFrontend));
                return;
            }

            if (_webView?.CoreWebView2 == null) return;

            try
            {
                var payload = new
                {
                    serverUrl = _config.ServerUrl,
                    actionText = _currentActionText,
                    players = _players.ToDictionary(k => k.Key, v => new
                    {
                        name = v.Value.Name,
                        balance = v.Value.Balance,
                        numericBalance = v.Value.NumericBalance,
                        isOnline = v.Value.IsOnline,
                        ping = v.Value.Ping,
                        world = v.Value.World,
                        avatarUrl = v.Value.AvatarUrl,
                        lastChecked = v.Value.LastChecked?.ToString("o"),
                        history = v.Value.History.Select(h => new { time = h.Time.ToString("o"), value = h.Value }).ToList()
                    }),
                    orders = _latestOrders
                };

                var msgObj = new { type = "sync", payload };
                var json = JsonSerializer.Serialize(msgObj);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }

        private void AppendLog(string msg)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendLog(msg)));
                return;
            }

            if (_webView?.CoreWebView2 == null) return;

            try
            {
                var msgObj = new { type = "log", text = msg };
                var json = JsonSerializer.Serialize(msgObj);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }

        /// <summary>
        /// TIẾN TRÌNH ĐIỀU PHỐI ĐỘC LẬP THÔNG MINH
        /// - Ưu tiên 1: Recheck TẤT CẢ TÊN Người Chơi mỗi 10 phút.
        /// - Ưu tiên 2: Khi KHÔNG quét tên ai (rảnh), tiến hành quét Order tuần tự liên tục
        /// </summary>
        private async Task MasterCoordinatorTickAsync()
        {
            if (_isBusyTask || !_isRunning) return;
            _isBusyTask = true;

            try
            {
                var now = DateTime.Now;
                var playerInterval = TimeSpan.FromMinutes(_config.PlayerRecheckMinutes);

                // ƯU TIÊN 1: Chu kỳ quét tên người chơi mỗi 10 phút
                if (now - _lastPlayerCheckTime >= playerInterval || _lastPlayerCheckTime == DateTime.MinValue)
                {
                    _currentActionText = "Đang Recheck Tên Người Chơi (Ưu tiên 10m)...";
                    SyncDataToFrontend();
                    AppendLog("════════════════════════════════════════════════════════════");
                    AppendLog("🚀 [ƯU TIÊN 1] BẮT ĐẦU CHU KỲ QUÉT NGƯỜI CHƠI (Mỗi 10 phút/lần)");
                    AppendLog("════════════════════════════════════════════════════════════");

                    var playerList = _players.Keys.ToList();
                    foreach (var playerName in playerList)
                    {
                        await CheckSinglePlayerAsync(playerName);
                        await Task.Delay(1200);
                    }

                    _lastPlayerCheckTime = DateTime.Now;
                    _currentActionText = "Rảnh - Chuyển sang quét Order liên tục";
                    SyncDataToFrontend();
                    AppendLog("✅ Đã hoàn thành chu kỳ quét người chơi. Tiếp theo: Quét Order liên tục khi rảnh.");
                    _isBusyTask = false;
                    return;
                }

                // ƯU TIÊN 2: Đang rảnh -> Quét Order tuần tự liên tục
                var nextItem = _orderItemList[_currentOrderItemIndex];
                _currentOrderItemIndex = (_currentOrderItemIndex + 1) % _orderItemList.Length;

                _currentActionText = $"Quét Order: {nextItem.ToUpper()} (Tuần tự liên tục)...";
                SyncDataToFrontend();

                await CheckSingleOrderItemAsync(nextItem);
                SyncDataToFrontend();
            }
            catch (Exception ex)
            {
                AppendLog($"⚠️ Ngoại lệ điều phối: {ex.Message}");
            }
            finally
            {
                _isBusyTask = false;
            }
        }

        private async Task CheckSinglePlayerAsync(string playerName)
        {
            var serverUrl = _config.ServerUrl.TrimEnd('/');
            var url = $"{serverUrl}/api/player/check?name={Uri.EscapeDataString(playerName)}";

            try
            {
                var res = await _httpClient.GetAsync(url);
                var json = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    AppendLog($"⚠️ Lỗi quét người chơi {playerName}: HTTP {res.StatusCode}");
                    return;
                }

                var data = JsonSerializer.Deserialize<PlayerCheckResponse>(json);
                if (data != null && data.success && _players.TryGetValue(playerName, out var state))
                {
                    state.Balance = data.balance ?? "N/A";
                    state.IsOnline = data.isOnline;
                    state.Ping = data.ping ?? "N/A";
                    state.World = data.world ?? "N/A";
                    state.AvatarUrl = data.avatarUrl ?? state.AvatarUrl;
                    state.LastChecked = DateTime.Now;

                    var numBal = ParsePrice(state.Balance);
                    state.NumericBalance = numBal;

                    state.History.Add(new BalancePoint { Time = DateTime.Now, Value = numBal });
                    if (state.History.Count > 100) state.History.RemoveAt(0);

                    AppendLog($"👤 [Player] {playerName}: {state.Balance} | {(state.IsOnline ? "ONLINE 🟢" : "OFFLINE 🔴")} | Ping: {state.Ping}");
                    SyncDataToFrontend();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Lỗi quét player {playerName}: {ex.Message}");
            }
        }

        private async Task CheckSingleOrderItemAsync(string itemKey)
        {
            var serverUrl = _config.ServerUrl.TrimEnd('/');

            if (itemKey == "redstone_pair")
            {
                var url = $"{serverUrl}/api/orders/redstone";
                try
                {
                    var res = await _httpClient.GetAsync(url);
                    var json = await res.Content.ReadAsStringAsync();
                    if (!res.IsSuccessStatusCode) return;

                    var data = JsonSerializer.Deserialize<RedstoneApiResponse>(json);
                    if (data != null && data.success)
                    {
                        _latestOrders["redstone_block"] = data.redstoneBlock?.orders ?? new List<OrderItem>();
                        _latestOrders["redstone"] = data.redstoneDust?.orders ?? new List<OrderItem>();
                        AnalyzeRedstoneAlerts(data);
                        AppendLog($"📦 [Order Market] REDSTONE: {data.redstoneBlock?.orders?.Count ?? 0} Block, {data.redstoneDust?.orders?.Count ?? 0} Dust ({data.redstoneBlock?.serverUsed ?? "Render 2 IP"}).");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"⚠️ Lỗi quét Redstone: {ex.Message}");
                }
                return;
            }

            var genericUrl = $"{serverUrl}/api/orders/item?name={Uri.EscapeDataString(itemKey)}";
            try
            {
                var res = await _httpClient.GetAsync(genericUrl);
                var json = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) return;

                var data = JsonSerializer.Deserialize<GenericOrderApiResponse>(json);
                if (data != null && data.success)
                {
                    _latestOrders[itemKey] = data.orders ?? new List<OrderItem>();
                    AppendLog($"📦 [Order Market] {itemKey.ToUpper()}: {data.orders?.Count ?? 0} đơn ({data.serverUsed ?? "Render"}).");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"⚠️ Lỗi quét order {itemKey}: {ex.Message}");
            }
        }

        private void AnalyzeRedstoneAlerts(RedstoneApiResponse data)
        {
            // 1. Redstone Block > 50,000
            if (data.redstoneBlock?.orders != null)
            {
                foreach (var o in data.redstoneBlock.orders)
                {
                    var priceVal = ParsePrice(o.price);
                    var delivered = o.delivered ?? "N/A";
                    var buyer = string.IsNullOrWhiteSpace(o.buyer) ? "Ẩn danh" : o.buyer;

                    if (priceVal > _config.RedstoneBlockThreshold)
                    {
                        var key = $"block_{buyer}_{o.price}_{delivered}";
                        if (!_notifiedOrders.Contains(key))
                        {
                            _notifiedOrders.Add(key);
                            string title = $"🔥 KÈO THƠM: Redstone Block > {_config.RedstoneBlockThreshold:N0}$/block";
                            string body = $"👤 Người mua: {buyer}\n💰 Giá: {o.price} / block\n📦 Đã giao: {delivered}\nServer: {data.redstoneBlock.serverUsed ?? "Render"}";
                            ShowWindowsToast(title, body);
                            AppendLog($"🚨 BẮN TOAST: {buyer} mua Redstone Block giá {o.price} (Đã giao: {delivered})");
                        }
                    }
                }
            }

            // 2. Redstone Dust <= 5,600
            if (data.redstoneDust?.orders != null)
            {
                foreach (var o in data.redstoneDust.orders)
                {
                    var priceVal = ParsePrice(o.price);
                    var delivered = o.delivered ?? "N/A";
                    var buyer = string.IsNullOrWhiteSpace(o.buyer) ? "Ẩn danh" : o.buyer;

                    if (priceVal > 0 && priceVal <= _config.RedstoneDustThreshold)
                    {
                        var key = $"dust_{buyer}_{o.price}_{delivered}";
                        if (!_notifiedOrders.Contains(key))
                        {
                            _notifiedOrders.Add(key);
                            string title = $"🎯 ORDER HỜI: Redstone Dust <= {_config.RedstoneDustThreshold:N0}$";
                            string body = $"👤 Người mua: {buyer}\n💰 Giá: {o.price} / redstone\n📦 Đã giao: {delivered}\nServer: {data.redstoneDust.serverUsed ?? "Render"}";
                            ShowWindowsToast(title, body);
                            AppendLog($"🚨 BẮN TOAST: {buyer} đặt Redstone Dust giá {o.price} <= 5.6k (Đã giao: {delivered})");
                        }
                    }
                }
            }
        }

        private double ParsePrice(string? priceStr)
        {
            if (string.IsNullOrWhiteSpace(priceStr)) return 0;
            try
            {
                var clean = priceStr.Replace("$", "").Replace(",", "").Trim();
                var match = Regex.Match(clean, @"\d+(?:\.\d+)?");
                if (match.Success && double.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                {
                    return val;
                }
            }
            catch { }
            return 0;
        }

        private void ShowWindowsToast(string title, string body)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(body)
                    .AddAttributionText("KingMC Supreme Watcher • Tr0ngX")
                    .Show();
            }
            catch
            {
                _trayIcon.ShowBalloonTip(5000, title, body, ToolTipIcon.Info);
            }
        }

        private void ShowAndRestore()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            BringToFront();
            Activate();

            // Refresh lại hiển thị webview nếu đang bị treo render do thu nhỏ
            try
            {
                if (_webView?.CoreWebView2 != null)
                {
                    SyncDataToFrontend();
                }
            }
            catch { }
        }

        private void ExitApp()
        {
            _isRunning = false;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configFile))
                {
                    var json = File.ReadAllText(_configFile);
                    _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFile, json);
            }
            catch { }
        }
    }
}
