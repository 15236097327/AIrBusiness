using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsPresentation;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using static FlightDispatchClient.MainWindow;
namespace FlightDispatchClient
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient client = new HttpClient();

        // 🌟 全局资金池
        private long playerFunds = 1000000000;
        private GMapMarker currentSelectedMarker = null;
        private System.Windows.Threading.DispatcherTimer gameTimer;
        private bool isGameOver = false;
        private bool isPaused = false;
        // 🌟 航线模式状态
        private bool isDrawingRoute = false;      // 是否处于拉线模式
        private GMapMarker routeStartMarker = null; // 连线的起点机场
        private GMapMarker elasticLine = null;     // 跟着鼠标走的“橡皮筋”虚线

        private long gameTickCount = 0;
        private Random randomEngine = new Random();
        private DateTime gameVirtualTime = new DateTime(2026, 1, 1, 6, 0, 0);
        private List<GameRouteInfo> activeRoutes = new List<GameRouteInfo>();

        private System.Windows.Threading.DispatcherTimer animationTimer;
        private List<ActiveFlight> flyingPlanes = new List<ActiveFlight>();
        private List<GameAirportInfo> allAirportsList = new List<GameAirportInfo>();
        private GMapMarker costPreviewMarker = null;
        private long totalTransportedPax = 0;

        private List<PlayerRecord> leaderboardData = new List<PlayerRecord>();
        public class UserAuthRequest
        {
            public string username { get; set; }
            public string password { get; set; }
        }
        public class ActiveFlight
        {
            public GMapMarker Marker { get; set; }       // 地图上的飞机图标
            public GMapMarker StartNode { get; set; }    // 起飞机场
            public GMapMarker EndNode { get; set; }      // 降落机场
            public double Progress { get; set; }         // 飞行进度 (0.0 到 1.0)
            public double Speed { get; set; }            // 飞行速度
            public int Passengers { get; set; }          // 搭载旅客
            public long ExpectedProfit { get; set; }     // 机票预期收益
            public string FlightCode { get; set; }       // 航班号
            public GamePlaneInfo PhysicalPlane { get; set; }
        }
        public class GameAirportInfo
        {
            public string Name { get; set; }
            public string Icao { get; set; }
            public int DailyPaxRate { get; set; }
            public int MaxCapacity { get; set; }
            public int Level { get; set; } = 1;
            public long Profit { get; set; } = 0;
            public int CurrentPassengers { get; set; } = 0;
            public Dictionary<string, int> Demands { get; set; } = new Dictionary<string, int>();

            // 🌟🌟🌟 核心重构：废弃 LocalFleetCount，改成真实的实体机队列表！
            public List<GamePlaneInfo> LocalFleet { get; set; } = new List<GamePlaneInfo>();
        }
        public class GamePlaneInfo
        {
            public string Id { get; set; }           // 飞机编号 (如 B-1024)
            public int Level { get; set; } = 1;      // 飞机等级 (1~3)

            // 根据等级动态获取载客量：Lv1=160人，Lv2=380人，Lv3=650人(宽体巨无霸)
            public int Capacity => Level == 1 ? 160 : (Level == 2 ? 380 : 650);

            // 升级费用：Lv1升Lv2=1.5亿，Lv2升Lv3=3亿
            public long UpgradeCost => Level == 1 ? 150000000 : (Level == 2 ? 300000000 : 0);
        }
        public class GameRouteInfo
        {
            public GMapMarker StartNode { get; set; } // 起点机场
            public GMapMarker EndNode { get; set; }   // 终点机场
            public double Distance { get; set; }      // 航线距离 (决定票价)
        }
        public class PlayerRecord
        {
            public string PlayerName { get; set; }
            public int SurvivedDays { get; set; }
            public long TransportedPax { get; set; }
            public long FinalFunds { get; set; }
        }
        // 🌟 新增：专门用于上传云端的单个机场详细数据
        public class AirportReport
        {
            public string AirportName { get; set; }
            public string Icao { get; set; }
            public int FinalLevel { get; set; }
            public int StrandedPax { get; set; }
            public int MaxCapacity { get; set; }
            public long TotalProfit { get; set; }
            public int FleetSize { get; set; }
            public int DailyPaxRate { get; set; }
        }

        // 🌟 新增：整场游戏的最终汇总报告
        public class GameReportPayload
        {
            public string PlayerName { get; set; }
            public int SurvivedDays { get; set; }
            public long FinalFunds { get; set; }
            public long TransportedPax { get; set; } // 🌟 补上运送人数
            public DateTime CreatedAt { get; set; }  // 🌟 记录创建时间
            public List<AirportReport> AirportStats { get; set; } = new List<AirportReport>();

            // 这是一个专门给 UI 绑定的属性，让时间显示得更漂亮
            public string FormattedDate => CreatedAt.ToString("yyyy-MM-dd HH:mm");
        }

        // 🌟 在类的最上方全局变量区，新增一个“剪贴板”用来暂存这局的数据
        private GameReportPayload currentSessionReport = null;

        private string leaderboardFilePath = "leaderboard.json"; // 数据持久化存盘文件
        private double CalculateDistance(PointLatLng start, PointLatLng end)
        {
            double e = 0.017453292519943295; // Math.PI / 180
            double a = start.Lng * e;
            double b = start.Lat * e;
            double c = end.Lng * e;
            double d = end.Lat * e;
            double f = Math.Sin(b) * Math.Sin(d) + Math.Cos(b) * Math.Cos(d) * Math.Cos(a - c);
            return 6371.0 * Math.Acos(Math.Min(1.0, f)); // 返回公里
        }
        // 🌟 升级版：动态局部机场数据结构


        public MainWindow()
        {
            try
            {
                InitializeComponent();

                // 初始化地图配置
                GMapProvider.WebProxy = null;
                // MainMap.MapProvider = GMapProviders.BingMap; // 使用必应地图
                MainMap.MapProvider = GMapProviders.BingSatelliteMap;
                MainMap.ShowTileGridLines = false;
                //MainMap.MapProvider = GMapProviders.BingHybridMap;
                MainMap.Position = new PointLatLng(35.0, 105.0); // 视角中心对准中国
                MainMap.MinZoom = 5;
                MainMap.MaxZoom = 8;
                MainMap.Zoom = 5;
                MainMap.MouseWheelZoomType = MouseWheelZoomType.MousePositionAndCenter;
                MainMap.CanDragMap = true;
                MainMap.DragButton = MouseButton.Right; // 右键拖动地图
                MainMap.ShowTileGridLines = false;

                // 🌟 核心设定：开启纯离线缓存模式！拔掉网线也能跑！
                // 第一次先联网缓冲，后面可以改成 CacheOnly
                // 🌟 初始化游戏主循环 (每 1 秒触发一次)
                gameTimer = new System.Windows.Threading.DispatcherTimer();
                gameTimer.Interval = TimeSpan.FromSeconds(1);
                gameTimer.Tick += GameTimer_Tick;
                MainMap.MouseMove += MainMap_MouseMove;

                animationTimer = new System.Windows.Threading.DispatcherTimer();
                animationTimer.Interval = TimeSpan.FromMilliseconds(50);
                animationTimer.Tick += AnimationTimer_Tick;
            }
            catch (Exception ex)
            {
                // 🌟 致命一击：强制拦截死前日志并弹窗！
                MessageBox.Show(
                    $"司令部严重故障！系统未能启动。\n\n" +
                    $"【错误原因】: {ex.Message}\n\n" +
                    $"【深层原因】: {ex.InnerException?.Message}\n\n" +
                    $"【崩溃位置】: {ex.StackTrace}",
                    "致命启动崩溃", MessageBoxButton.OK, MessageBoxImage.Error);

                Application.Current.Shutdown();
            }

        }

        // 🌟 1. 注册按钮逻辑
        private async void BtnRegister_Click(object sender, RoutedEventArgs e)
        {
            string user = TxtLoginUsername.Text.Trim();
            string pass = TxtLoginPassword.Password; // 注意 PasswordBox 取值方式不同

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                MessageBox.Show("代号和密码不能为空！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var request = new UserAuthRequest { username = user, password = pass };
            string json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                using (var client = new HttpClient())
                {
                    // 🚨 换成你的公网 IP
                    HttpResponseMessage res = await client.PostAsync("http://124.220.178.183:8084/api/user/register", content);
                    string reply = await res.Content.ReadAsStringAsync();

                    if (res.IsSuccessStatusCode) MessageBox.Show(reply, "注册成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    else MessageBox.Show(reply, "注册失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) { MessageBox.Show("连接服务器失败：" + ex.Message); }
        }

        // 🌟 2. 登录按钮逻辑
        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string user = TxtLoginUsername.Text.Trim();
            string pass = TxtLoginPassword.Password;

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) return;

            BtnLogin.Content = "📡 验证中...";
            BtnLogin.IsEnabled = false;

            var request = new UserAuthRequest { username = user, password = pass };
            string json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                using (var client = new HttpClient())
                {
                    // 🚨 换成你的公网 IP
                    HttpResponseMessage res = await client.PostAsync("http://124.220.178.183:8084/api/user/login", content);

                    if (res.IsSuccessStatusCode)
                    {
                        // 登录成功！
                        string loggedInName = await res.Content.ReadAsStringAsync();

                        // 1. 把游戏结算界面的那个名字框，强行绑定为登录账号，且不让修改！
                        TxtPlayerName.Text = loggedInName;
                        TxtPlayerName.IsReadOnly = true;

                        // 2. 隐藏安检大门，进入游戏指挥中心！
                        LoginOverlay.Visibility = Visibility.Collapsed;
                        MessageBox.Show($"欢迎回来，指挥官 {loggedInName}！", "认证通过", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        string errorMsg = await res.Content.ReadAsStringAsync();
                        MessageBox.Show(errorMsg, "认证拒绝", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("连接服务器失败：" + ex.Message); }
            finally
            {
                BtnLogin.Content = "🔓 登 录 调 度 系 统";
                BtnLogin.IsEnabled = true;
            }
        }

        // 🌟 3. 退出按钮
        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown(); // 直接强关游戏
        }
        // 🌟 新增：游戏主循环（心脏跳动）
        private void GameTimer_Tick(object sender, EventArgs e)
        {
            if (isGameOver) return;
            gameTickCount++;

            // 🌟 1. 推进虚拟时间：每次 Tick (1秒) = 游戏里过了 15 分钟
            gameVirtualTime = gameVirtualTime.AddMinutes(15);

            // 🌟 2. 炫酷 UI：在软件窗口标题栏实时显示游戏日历和存活天数！
            int survivedDays = (gameVirtualTime - new DateTime(2026, 1, 1)).Days;
            this.Title = $"✈ 航空大亨指挥中心 | 当前游戏时间: {gameVirtualTime:yyyy年MM月dd日 HH:mm} | 🏆 已存活: {survivedDays} 天";

            // 🌟 3. 真实的昼夜潮汐：根据虚拟时钟的“小时”来算早晚高峰！
            // 早上 8 点到晚上 20 点是高峰 (Multiplier > 1)，半夜是低谷 (Multiplier < 1)
            double timeMultiplier = 1.0 + 0.8 * Math.Sin((gameVirtualTime.Hour - 8) / 24.0 * Math.PI * 2);
            // 1. 遍历地图上所有的机场红点
            foreach (var marker in MainMap.Markers)
            {
                if (marker.Shape is FrameworkElement element && element.Tag is GameAirportInfo info)
                {
                    // 旅客不断涌入航站楼！(数值模拟生成)
                    // ================= 算法核心 2：随机扰动与目的地分配 =================
                    double randomFactor = 0.8 + (randomEngine.NextDouble() * 0.4);
                    int actualArrivals = (int)(info.DailyPaxRate * timeMultiplier * randomFactor);

                    if (actualArrivals > 0)
                    {
                        // 🌟🌟🌟 核心升级：引力模型 (Gravity Model) 权重轮盘赌算法
                        // 计算全国所有机场的“吸引力总和”
                        int totalWeight = allAirportsList.Sum(a => a.DailyPaxRate);
                        int randValue = randomEngine.Next(totalWeight);

                        GameAirportInfo destInfo = allAirportsList.Last(); // 兜底变量

                        // 轮盘赌抽奖：体量越大的机场，占的权重区间越大，越容易被抽中！
                        foreach (var candidate in allAirportsList)
                        {
                            randValue -= candidate.DailyPaxRate;
                            if (randValue <= 0)
                            {
                                destInfo = candidate;
                                break;
                            }
                        }

                        // 逻辑防错：旅客不能想去自己现在所在的机场
                        if (destInfo.Name == info.Name)
                        {
                            // 如果恰好抽到了自己，就强行让他去全国最大的枢纽（模拟现实中的干线汇聚效应）
                            destInfo = allAirportsList.OrderByDescending(a => a.DailyPaxRate).First(a => a.Name != info.Name);
                        }

                        // 如果账本里还没有这个目的地，先建个户头
                        if (!info.Demands.ContainsKey(destInfo.Name))
                            info.Demands[destInfo.Name] = 0;

                        // 旅客进入对应的候机厅
                        info.Demands[destInfo.Name] += actualArrivals;

                        // 总人数依然增加（用于判定是否破产）
                        info.CurrentPassengers += actualArrivals;
                    }



                    // 2. 致命判定：如果滞留人数超过了机场的承载极限...
                    if (info.CurrentPassengers >= info.MaxCapacity)
                    {
                        TriggerGameOver(info);
                        return; // 立刻停止本轮循环
                    }

                }
            }
            long tickTotalProfit = 0; // 记录这一秒全中国赚了多少钱

            // ==============================================================
            // 🌟 算法核心 3：航线派单调度 (满足条件直接刷出实体飞机！) 
            // ==============================================================
            // 为了防止同秒钟刷出太多飞机，我们设定每 3 秒 (gameTickCount % 3 == 0) 进行一次全网航班调度
            if (gameTickCount % 3 == 0)
            {
                // 1. 将全网航线按“起点机场”分组，防止飞机克隆
                var routesByStartNode = activeRoutes.GroupBy(r => r.StartNode);

                foreach (var group in routesByStartNode)
                {
                    var startMarker = group.Key;
                    var startInfo = (startMarker.Shape as FrameworkElement).Tag as GameAirportInfo;

                    // 这个机场本轮最多能起飞的班次 = 驻扎的飞机数量
                    int availablePlanes = startInfo.LocalFleet.Count;
                    if (availablePlanes <= 0) continue; // 没飞机就不排班

                    // 2. 🌟 核心修复：智能优先级排序！
                    // 把该机场接通的所有航线，按照【目的地排队人数】从高到低排序！
                    var sortedRoutes = group.OrderByDescending(r =>
                    {
                        var endInfo = (r.EndNode.Shape as FrameworkElement).Tag as GameAirportInfo;
                        return startInfo.Demands.ContainsKey(endInfo.Name) ? startInfo.Demands[endInfo.Name] : 0;
                    }).ToList();

                    // 3. 按照优先级，依次给最危急的航线派发实体飞机
                    // 派单循环
                    foreach (var route in sortedRoutes)
                    {
                        // 🌟 1. 检查真实机库，没飞机了立刻停止派单！
                        if (startInfo.LocalFleet.Count <= 0) break;

                        var endInfo = (route.EndNode.Shape as FrameworkElement).Tag as GameAirportInfo;
                        int waitingForThisRoute = startInfo.Demands.ContainsKey(endInfo.Name) ? startInfo.Demands[endInfo.Name] : 0;

                        if (waitingForThisRoute > 50)
                        {
                            // 🌟 2. 真正的物理提车！从原机场机库彻底删掉这架飞机！
                            var planeToUse = startInfo.LocalFleet[0];
                            startInfo.LocalFleet.RemoveAt(0);

                            int maxCapacity = planeToUse.Capacity;
                            int paxToMove = Math.Min(maxCapacity, waitingForThisRoute);

                            startInfo.Demands[endInfo.Name] -= paxToMove;
                            startInfo.CurrentPassengers -= paxToMove;

                            long expectedProfit = (long)(paxToMove * route.Distance * 1.5);
                            double dy = startMarker.Position.Lat - route.EndNode.Position.Lat;
                            double dx = route.EndNode.Position.Lng - startMarker.Position.Lng;
                            double angle = Math.Atan2(dy, dx) * 180 / Math.PI + 45;

                            double visualSize = 22 + (planeToUse.Level * 8);

                            TextBlock planeVisual = new TextBlock
                            {
                                Text = "✈",
                                FontSize = visualSize,
                                Foreground = Brushes.White,
                                RenderTransformOrigin = new Point(0.5, 0.5),
                                RenderTransform = new RotateTransform(angle),
                                Cursor = Cursors.Hand
                            };

                            GMapMarker planeMarker = new GMapMarker(startMarker.Position)
                            {
                                Shape = planeVisual,
                                Offset = new Point(-visualSize / 2, -visualSize / 2),
                                ZIndex = 1000
                            };

                            ActiveFlight newFlight = new ActiveFlight
                            {
                                Marker = planeMarker,
                                StartNode = startMarker,
                                EndNode = route.EndNode,
                                Progress = 0,
                                Speed = 60.0 / route.Distance,
                                Passengers = paxToMove,
                                ExpectedProfit = expectedProfit,
                                FlightCode = planeToUse.Id,

                                // 🌟 3. 把这架实体飞机绑在航班上一起飞走！
                                PhysicalPlane = planeToUse
                            };
                            planeVisual.Tag = newFlight; // 把航班数据塞给这个图标
                            planeVisual.MouseLeftButtonDown += Flight_MouseLeftButtonDown; // 绑定鼠标点击事件
                            MainMap.Markers.Add(planeMarker);
                            flyingPlanes.Add(newFlight);

                            // 🌟 刷新出发机场面板，你会看到飞机真的少了一架！
                            if (currentSelectedMarker == startMarker) RefreshPanelUI(startInfo);
                        }
                    }
                }
            }

            // 如果这秒钟赚到钱了，让金币特效在 UI 上跳动！
            if (tickTotalProfit > 0)
            {
                TxtFunds.Text = $"💰 资金: {playerFunds:N0} (+{tickTotalProfit:N0})";
                TxtFunds.Foreground = Brushes.LimeGreen; // 赚钱时变绿
            }
            else
            {
                TxtFunds.Text = $"💰 资金: {playerFunds:N0}";
                TxtFunds.Foreground = Brushes.Gold; // 没赚钱时恢复金色
            }

            // 刷新正在查看的面板 UI
            if (currentSelectedMarker != null && AirportInfoPanel.Visibility == Visibility.Visible)
            {
                GameAirportInfo currentInfo = (currentSelectedMarker.Shape as FrameworkElement).Tag as GameAirportInfo;
                RefreshPanelUI(currentInfo);
            }
            // ==============================================================
            // 🌟 视觉引擎更新：刷新左侧高危榜单 & 地图节点变色
            // ==============================================================
            // 1. 刷新左侧危险排行榜 (按拥挤度百分比降序，取前 8 名最危险的)
            var urgentList = allAirportsList
                .OrderByDescending(a => (double)a.CurrentPassengers / a.MaxCapacity)
                .ToList();

            UrgentAirportsList.ItemsSource = null; // 清空旧数据
            UrgentAirportsList.ItemsSource = urgentList; // 绑定新排行榜

            // 2. 地图雷达变色警报机制
            foreach (var marker in MainMap.Markers)
            {
                if (marker.Shape is Canvas canvas && canvas.Tag is GameAirportInfo info)
                {
                    // 找到 Canvas 里的那个红点 (Ellipse)
                    var dot = canvas.Children.OfType<System.Windows.Shapes.Ellipse>().FirstOrDefault();
                    if (dot != null)
                    {
                        double dangerRatio = (double)info.CurrentPassengers / info.MaxCapacity;

                        // 🟢 安全 (<50%)
                        if (dangerRatio < 0.5) dot.Fill = Brushes.MediumSeaGreen;
                        // 🟠 警告 (50%~80%)
                        else if (dangerRatio < 0.8) dot.Fill = Brushes.DarkOrange;
                        // 🔴 极度危险 (>80%) - 准备爆仓！
                        else dot.Fill = Brushes.Crimson;
                    }
                }
            }
        }
        // 🌟 核心控制：战术暂停与继续 (子弹时间)
        private void PauseGameBtn_Click(object sender, RoutedEventArgs e)
        {
            // 如果已经破产或者还没开局，不响应暂停
            if (isGameOver || flyingPlanes == null) return;

            isPaused = !isPaused; // 切换状态

            if (isPaused)
            {
                // 1. 冻结时空：停掉游戏逻辑和动画渲染
                gameTimer.Stop();
                animationTimer.Stop();

                // 2. 视觉警告：按钮变红，提示玩家当前处于静止状态
                PauseGameBtn.Content = "▶️ 继续游戏 (战术暂停中)";
                PauseGameBtn.Background = Brushes.Crimson;

                // LogListBox.Items.Add($"\n[{DateTime.Now:HH:mm:ss}] ⏸️ 司令部指令：时间线已冻结！您可以从容进行资产调度。");
            }
            else
            {
                // 1. 解除冻结：时间重新流动
                gameTimer.Start();
                animationTimer.Start();

                // 2. 视觉恢复：按钮变回橙色
                PauseGameBtn.Content = "⏸️ 暂停游戏";
                PauseGameBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF8C00"));

                // LogListBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] ▶️ 司令部指令：时间线恢复流动！");
            }
        }
        // 🌟 战术瞬移：点击左侧排行榜，镜头直接飞向该机场！
        private void UrgentAirportsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UrgentAirportsList.SelectedItem is GameAirportInfo selectedInfo)
            {
                // 去地图上把这个机场找出来
                var targetMarker = MainMap.Markers.FirstOrDefault(m =>
                    m.Shape is FrameworkElement element &&
                    (element.Tag as GameAirportInfo)?.Name == selectedInfo.Name);

                if (targetMarker != null)
                {
                    // 1. 镜头瞬间居中到该机场！
                    MainMap.Position = targetMarker.Position;

                    // 2. 自动将其设为选中状态，并展开控制面板
                    currentSelectedMarker = targetMarker;
                    RefreshPanelUI(selectedInfo);
                    AirportInfoPanel.Visibility = Visibility.Visible;
                }

                // 取消列表选中状态，允许玩家下次再点它
                UrgentAirportsList.SelectedItem = null;
            }
        }

        // 🌟 核心引擎：处理空域中所有飞机的平滑飞行与降落结算
        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            if (isGameOver) return;
            if (flyingPlanes == null) return; // 安全检查

            // 🌟 核心引擎：倒序遍历！最完美的边遍历边删除方案
            for (int i = flyingPlanes.Count - 1; i >= 0; i--)
            {
                var flight = flyingPlanes[i];
                flight.Progress += flight.Speed; // 飞机往前飞

                if (flight.Progress >= 1.0)
                {
                    // 1. 资金到账，战绩+1
                    playerFunds += flight.ExpectedProfit;
                    totalTransportedPax += flight.Passengers;

                    // ==========================================
                    // 🌟🌟🌟 核心修复：实体飞机降落入库！
                    // ==========================================
                    var endInfo = (flight.EndNode.Shape as FrameworkElement).Tag as GameAirportInfo;
                    if (flight.PhysicalPlane != null && endInfo != null)
                    {
                        endInfo.LocalFleet.Add(flight.PhysicalPlane); // 飞机物理存入目的地！

                        // 如果玩家正好点开了目的地机场的面板，实时刷新让他看到飞机入库！
                        if (currentSelectedMarker == flight.EndNode)
                        {
                            RefreshPanelUI(endInfo);
                        }
                    }

                    // 2. 从地图上把这架飞机的图标擦除
                    if (MainMap.Markers.Contains(flight.Marker))
                    {
                        MainMap.Markers.Remove(flight.Marker);
                    }

                    // 3. 💥 致命修复：直接从现役航班列表中干掉它！
                    // 因为是倒序循环，所以直接用 RemoveAt(i) 绝对安全，连垃圾车都省了！
                    flyingPlanes.RemoveAt(i);
                }
                else
                {
                    // =========================================================
                    // ✈️ 飞机物理引擎升级：沿着贝塞尔弧线飞行！
                    // =========================================================
                    double startLat = flight.StartNode.Position.Lat;
                    double startLng = flight.StartNode.Position.Lng;
                    double endLat = flight.EndNode.Position.Lat;
                    double endLng = flight.EndNode.Position.Lng;

                    // 这里的算法必须和画线那里的算法保持一模一样
                    double midLat = (startLat + endLat) / 2.0;
                    double midLng = (startLng + endLng) / 2.0;
                    double dx = endLng - startLng;
                    double dy = endLat - startLat;

                    double curvature = 0.2; // 保持和画线时的曲率 0.2 一致
                    double ctrlLat = midLat + dx * curvature;
                    double ctrlLng = midLng - dy * curvature;

                    // 将 Progress (0~1) 作为贝塞尔曲线的参数 t
                    double t = flight.Progress;
                    double u = 1 - t;

                    // 根据此时的飞行进度，计算出飞机在弧线上的精确经纬度
                    double curLat = (u * u * startLat) + (2 * u * t * ctrlLat) + (t * t * endLat);
                    double curLng = (u * u * startLng) + (2 * u * t * ctrlLng) + (t * t * endLng);

                    flight.Marker.Position = new PointLatLng(curLat, curLng);
                }
            }

            // 🌟 (可选) 刷新顶部的大盘资金 UI (假设你有一个叫 UpdateTopHUD 的方法，没有的话可以忽略这行)
            // UpdateTopHUD();
        }

        // 🌟 事件拦截：点击空中的飞机查看实时数据
        private void Flight_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // 防止点击穿透到地图上
            if (sender is FrameworkElement element && element.Tag is ActiveFlight flight)
            {
                var startInfo = (flight.StartNode.Shape as FrameworkElement).Tag as GameAirportInfo;
                var endInfo = (flight.EndNode.Shape as FrameworkElement).Tag as GameAirportInfo;

                // 弹窗会自然挂起 UI 线程，形成酷炫的“战术子弹时间”！
                MessageBox.Show(
                    $"✈ 实时航班雷达追踪: {flight.FlightCode}\n" +
                    $"------------------------------------\n" +
                    $"🛫 始发航站楼: {startInfo.Name}\n" +
                    $"🛬 目标航站楼: {endInfo.Name}\n\n" +
                    $"👥 当前搭载旅客: {flight.Passengers:N0} 人\n" +
                    $"💰 预计抵达收益: ¥ {flight.ExpectedProfit:N0}\n\n" +
                    $"📍 航程完成度: {(flight.Progress * 100):F1} %",
                    "空中管制雷达 (ATC)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        // 🌟 新增：Game Over 裁决逻辑
        // 🌟 修改：Game Over 裁决逻辑与按钮状态切换
        // 🌟 终极版：Game Over 裁决逻辑与破产清算报告导出
        // 🌟 终极版：Game Over 裁决逻辑与破产清算报告导出 (完美兼容主动结束)
        // 🌟 终极版：云原生 Game Over 裁决逻辑 (不主动写本地文件)
        private void TriggerGameOver(GameAirportInfo crashedAirport)
        {
            isGameOver = true;
            if (gameTimer != null) gameTimer.Stop();
            if (animationTimer != null) animationTimer.Stop();

            long totalProfit = 0;
            long totalStranded = 0;
            int totalFleet = 0;
            int survivedDays = (gameVirtualTime - new DateTime(2026, 1, 1)).Days;

            // ================= 1. 将数据收集到“内存集装箱”中 =================
            currentSessionReport = new GameReportPayload
            {
                SurvivedDays = survivedDays,
                FinalFunds = playerFunds,
                AirportStats = new List<AirportReport>()
            };

            foreach (var marker in MainMap.Markers)
            {
                if (marker.Shape is FrameworkElement element && element.Tag is GameAirportInfo info)
                {
                    currentSessionReport.AirportStats.Add(new AirportReport
                    {
                        AirportName = info.Name,
                        Icao = info.Icao,
                        FinalLevel = info.Level,
                        StrandedPax = info.CurrentPassengers,
                        MaxCapacity = info.MaxCapacity,
                        TotalProfit = info.Profit,
                        FleetSize = info.LocalFleet.Count,
                        DailyPaxRate = info.DailyPaxRate
                    });

                    totalProfit += info.Profit;
                    totalStranded += info.CurrentPassengers;
                    totalFleet += info.LocalFleet.Count;
                }
            }

            // ================= 2. 渲染 UI =================
            if (crashedAirport != null)
            {
                TxtFailReason.Text = $"💥 爆仓节点: 【{crashedAirport.Name}】\n滞留人数突破了 {crashedAirport.MaxCapacity:N0} 的极限，航站楼彻底瘫痪！";
                TxtFailReason.Foreground = Brushes.Orange;
            }
            else
            {
                TxtFailReason.Text = "🛑 营运中止: 指挥官已主动结束本次调度任务，资产锁定完毕。";
                TxtFailReason.Foreground = Brushes.LightSkyBlue;
            }

            TxtFinalDays.Text = $"🏆 存活时间: {survivedDays} 天";
            TxtFinalPax.Text = $"👥 累计运送: {totalTransportedPax:N0} 人次";
            TxtFinalFunds.Text = $"💰 最终结余: ¥ {playerFunds:N0}";

            GameOverOverlay.Visibility = Visibility.Visible;
            LoadDataBtn.Content = "🔄 开启下一局 (Restart)";
            LoadDataBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC"));

            // ================= 3. 弹出结账单 =================
            string msgHeader = crashedAirport != null
                ? $"惨剧发生！\n\n【{crashedAirport.Name}】机场发生严重踩踏，公司破产。\n\n"
                : $"营运结束！\n\n资产已盘点完毕。\n\n";

            MessageBox.Show(
                msgHeader +
                $"📊 【清算总结】\n" +
                $"- 剩余资金: ¥ {playerFunds:N0}\n" +
                $"- 总滞留: {totalStranded:N0} 人\n" +
                $"- 总机队: {totalFleet} 架\n\n" +
                $"⚠️ 数据暂存于终端内存。请在后方大屏决定是否将详细数据【上传至云端】！",
                "结算完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // 🌟 新增：刷新局部控制台 UI 的专用方法
        private void RefreshPanelUI(GameAirportInfo info)
        {
            TxtAirportName.Text = $"📍 {info.Name} ({info.Icao})";
            TxtAirportLevel.Text = $"⭐ 等级: Lv.{info.Level} (容量: {info.MaxCapacity:N0})";
            TxtAirportPax.Text = $"👥 当前滞留: {info.CurrentPassengers:N0} 人";

            // 1. 渲染 O-D 情报雷达
            var topDemands = info.Demands.OrderByDescending(kv => kv.Value).Take(3).ToList();
            if (topDemands.Count == 0) TxtDestinations.Text = "📡 雷达侦听: 暂无旅客排队";
            else
            {
                TxtDestinations.Text = "📡 高频目的地排队情报:\n";
                foreach (var demand in topDemands) TxtDestinations.Text += $"   🚩 去 {demand.Key} : {demand.Value:N0} 人\n";
            }

            // 2. 🌟 动态生成机队列表及升级按钮
            FleetContainer.Children.Clear();
            if (info.LocalFleet.Count == 0)
            {
                FleetContainer.Children.Add(new TextBlock { Text = "   暂无驻扎飞机，运力停摆！", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });
            }
            else
            {
                foreach (var plane in info.LocalFleet)
                {
                    Button planeBtn = new Button
                    {
                        Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 40)),
                        Foreground = Brushes.White,
                        Margin = new Thickness(0, 2, 0, 2),
                        Padding = new Thickness(5),
                        Cursor = Cursors.Hand,
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };

                    string btnText = $"✈ {plane.Id} | Lv.{plane.Level} (当前运力: {plane.Capacity}人)\n";
                    if (plane.Level < 3)
                        btnText += $"   ✨ 点击升级Lv.{plane.Level + 1} (满载{(plane.Level == 1 ? 380 : 650)}人) | 需 ¥{plane.UpgradeCost / 10000:N0}万";
                    else
                        btnText += $"   👑 已达满级 (超大型宽体客机)";

                    planeBtn.Content = btnText;

                    // 给这架特定飞机绑定升级事件！
                    if (plane.Level < 3)
                    {
                        planeBtn.Click += (s, e) =>
                        {
                            if (playerFunds >= plane.UpgradeCost)
                            {
                                playerFunds -= plane.UpgradeCost;
                                plane.Level++;
                                TxtFunds.Text = $"💰 资金: {playerFunds:N0}";
                                //LogListBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] 🔧 资产升级！【{plane.Id}】已改造为 Lv.{plane.Level}，单次运力暴增至 {plane.Capacity} 人！");
                                RefreshPanelUI(info); // 刷新UI
                            }
                            else MessageBox.Show($"资金不足！升级需要 ¥{plane.UpgradeCost:N0}");
                        };
                    }
                    FleetContainer.Children.Add(planeBtn);
                }
            }
        }
        // 🌟 载入航空大亨世界
        // 🌟 修改：载入世界与“重新开局”的完美融合
        // 🌟 最终版：载入世界与“重新开局”的完美融合 (包含算法修正)
        // 🌟 终极完整版：载入世界、重新开局、戴墨镜滤镜、科学客流、常驻文字标签
        // 🌟 终极完整版：载入世界、备战等待、主动结算的三段式状态机
        private async void LoadDataBtn_Click(object sender, RoutedEventArgs e)
        {
            // ==========================================
            // 🌟 状态 1：游戏正在进行中 -> 玩家点击了【主动结算】
            // ==========================================
            if (allAirportsList.Count > 0 && gameTimer.IsEnabled && !isGameOver)
            {
                // 🌟 1. 弹窗前，强制冻结时空！（防止玩家犹豫期间背后爆仓）
                gameTimer.Stop();
                animationTimer.Stop();

                MessageBoxResult result = MessageBox.Show(
                    "指挥官，您确定要结束当前的调度任务吗？\n\n点击【是】将立刻进行资产盘点并锁定战绩，所有航线与航班将永久停止运作。\n点击【否】返回指挥中心继续调度。",
                    "🛑 营运结算确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    // 🌟 2. 玩家心意已决，执行清算
                    TriggerGameOver(null);
                }
                else
                {
                    // 🌟 3. 玩家反悔了！解除时空冻结，继续游戏
                    gameTimer.Start();
                    animationTimer.Start();
                }

                return; // 无论选是还是否，都打断方法，绝不往下执行重开世界的代码
            }

            // ==========================================
            // 🌟 状态 2：世界已载入，处于“备战静止状态” -> 玩家点击了【开始调度】
            // ==========================================
            if (allAirportsList.Count > 0 && !gameTimer.IsEnabled && !isGameOver)
            {
                // 正式点火起飞！
                gameTimer.Start();
                animationTimer.Start();

                // 按钮变成红色的结算预警
                LoadDataBtn.Content = "🛑 结束游戏 (主动结算)";
                LoadDataBtn.Background = Brushes.Crimson;
                return; // 打断，进入游戏主循环
            }

            // ==========================================
            // 🌟 状态 3：空盘状态或Game Over后 -> 重置并【载入新世界】
            // ==========================================
            // 强制按停所有计时器
            if (gameTimer != null) gameTimer.Stop();
            if (animationTimer != null) animationTimer.Stop();

            isGameOver = false;
            isPaused = false;

            if (PauseGameBtn != null)
            {
                PauseGameBtn.Visibility = Visibility.Visible;
                PauseGameBtn.Content = "⏸️ 战术暂停 (Bullet Time)";
                PauseGameBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF8C00"));
            }

            // 重置宇宙常量
            gameTickCount = 0;
            gameVirtualTime = new DateTime(2026, 1, 1, 6, 0, 0);
            playerFunds = 1000000000;
            totalTransportedPax = 0;
            GameOverOverlay.Visibility = Visibility.Collapsed;
            if (BtnUploadReport != null)
            {
                BtnUploadReport.IsEnabled = true;
                BtnUploadReport.Content = "📤 授权并上传详细数据";
            }
            TxtFunds.Text = $"💰 资金: {playerFunds:N0}";
            TxtFunds.Foreground = Brushes.Gold;

            // 清理残骸
            currentSelectedMarker = null;
            AirportInfoPanel.Visibility = Visibility.Collapsed;
            isDrawingRoute = false;
            routeStartMarker = null;
            if (elasticLine != null) MainMap.Markers.Remove(elasticLine);

            foreach (var plane in flyingPlanes) MainMap.Markers.Remove(plane.Marker);
            flyingPlanes.Clear();
            activeRoutes.Clear();
            allAirportsList.Clear();
            UrgentAirportsList.ItemsSource = null;
            MainMap.Markers.Clear();

            // 按钮防手抖提示
            LoadDataBtn.Content = "🌐 重新构建世界中...";
            LoadDataBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC"));

            // 给地球戴墨镜
            List<PointLatLng> asiaPoints = new List<PointLatLng>
            {
                new PointLatLng(65, 70), new PointLatLng(65, 145),
                new PointLatLng(10, 145), new PointLatLng(10, 70)
            };
            GMapPolygon darkOverlay = new GMapPolygon(asiaPoints)
            {
                Shape = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(215, 10, 10, 15)), IsHitTestVisible = false }
            };
            MainMap.Markers.Add(darkOverlay);

            // 请求数据与生成雷达节点
            try
            {
                //HttpResponseMessage response = await client.GetAsync("http://127.0.0.1:8080/tycoon/airports");
                //response.EnsureSuccessStatusCode();
                //string responseBody = await response.Content.ReadAsStringAsync();
                string filePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "airports.json");
                string responseBody = System.IO.File.ReadAllText(filePath);
                using (JsonDocument doc = JsonDocument.Parse(responseBody))
                {
                    JsonElement dataArray = doc.RootElement.GetProperty("data");
                    foreach (JsonElement ap in dataArray.EnumerateArray())
                    {
                        string name = ap.GetProperty("airport_name").GetString();
                        string icao = ap.GetProperty("icao_code").GetString();
                        double lat = ap.GetProperty("latitude").GetDouble();
                        double lon = ap.GetProperty("longitude").GetDouble();
                        int pax = ap.GetProperty("daily_pax_base").GetInt32();

                        double minSize = 8.0; double maxSize = 35.0;
                        double scale = (double)pax / 250000.0; if (scale > 1.0) scale = 1.0;
                        double dotSize = minSize + scale * (maxSize - minSize);

                        GMapMarker marker = new GMapMarker(new PointLatLng(lat, lon));

                        Ellipse redDot = new Ellipse
                        {
                            Width = dotSize,
                            Height = dotSize,
                            Fill = Brushes.Crimson,
                            Stroke = Brushes.White,
                            StrokeThickness = scale > 0.5 ? 2 : 1,
                            Cursor = Cursors.Hand
                        };

                        TextBlock nameLabel = new TextBlock
                        {
                            Text = name,
                            Foreground = new SolidColorBrush(Color.FromArgb(200, 220, 220, 220)),
                            FontSize = 11,
                            FontWeight = FontWeights.Bold,
                            IsHitTestVisible = false
                        };

                        Canvas markerVisual = new Canvas();
                        markerVisual.Children.Add(redDot);
                        markerVisual.Children.Add(nameLabel);
                        Canvas.SetLeft(nameLabel, dotSize + 4);
                        Canvas.SetTop(nameLabel, (dotSize - 15) / 2);

                        int gameRate = Math.Max(2, pax / 96);
                        int dynamicCapacity = 10000 + (int)(pax * 1.5);

                        redDot.Tag = new GameAirportInfo
                        {
                            Name = name,
                            Icao = icao,
                            DailyPaxRate = gameRate,
                            MaxCapacity = dynamicCapacity
                        };
                        redDot.DataContext = marker;
                        redDot.MouseLeftButtonDown += RedDot_MouseLeftButtonDown;

                        marker.Shape = markerVisual;
                        markerVisual.Tag = redDot.Tag;
                        marker.Offset = new Point(-dotSize / 2, -dotSize / 2);

                        MainMap.Markers.Add(marker);
                        allAirportsList.Add(redDot.Tag as GameAirportInfo);
                    }

                    // ==========================================
                    // 🌟 致命改变：取消自动点火！把按钮变成“备战状态”
                    // ==========================================
                    // gameTimer.Start();      <-- 删掉！
                    // animationTimer.Start(); <-- 删掉！

                    LoadDataBtn.Content = "▶️ 开始调度 (Start Game)";
                    LoadDataBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E8B57")); // 醒目的绿色起飞按钮
                }
            }
            catch (Exception)
            {
                LoadDataBtn.Content = "🌐 载入世界失败 (重试)";
                LoadDataBtn.Background = Brushes.Crimson;
            }
        }
        // 🌟 点击红点：展开局部控制台
        // 🌟 简化后的点击红点事件
        private void RedDot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (isGameOver) return;

            if (sender is FrameworkElement element && element.Tag is GameAirportInfo info)
            {
                // 🌟 核心：如果是连线模式下的“第二次点击”
                if (isDrawingRoute && routeStartMarker != null)
                {
                    CompleteRoute(routeStartMarker, element.DataContext as GMapMarker);
                    return;
                }

                // 常规点击逻辑...
                currentSelectedMarker = element.DataContext as GMapMarker;
                RefreshPanelUI(info);
                AirportInfoPanel.Visibility = Visibility.Visible;

                //LogListBox.Items.Add($"\n[{DateTime.Now:HH:mm:ss}] 📡 【{info.Name}】候机大厅旅客流向侦听报告：");

                // 使用 LINQ 把字典按人数降序排列，取前 3 名
                var topDemands = info.Demands.OrderByDescending(kv => kv.Value).Take(3).ToList();

                if (topDemands.Count == 0)
                {
                    // LogListBox.Items.Add($"   -> 暂无滞留旅客。");
                }
                else
                {
                    foreach (var demand in topDemands)
                    {
                        //LogListBox.Items.Add($"   -> 🚩 前往 【{demand.Key}】 : 等待人数 {demand.Value:N0} 人");
                    }
                }
            }
        }
        // 🌟 核心升级：生成双向平行且不重叠的航空航线
        // 🌟 核心升级：生成完美的贝塞尔曲线弧形航线
        private void CompleteRoute(GMapMarker start, GMapMarker end)
        {
            isDrawingRoute = false;
            if (elasticLine != null) MainMap.Markers.Remove(elasticLine);

            if (start == end) return;
            bool routeExists = activeRoutes.Any(r => r.StartNode == start && r.EndNode == end);
            if (routeExists)
            {
                var startInfo = (start.Shape as FrameworkElement).Tag as GameAirportInfo;
                var endInfo = (end.Shape as FrameworkElement).Tag as GameAirportInfo;
                MessageBox.Show($"司令部提示：\n【{startInfo.Name}】飞往【{endInfo.Name}】的航线已存在！\n请勿重复投资！", "重复建设拦截");
                return; // 直接打断施法，不扣钱，不画线！
            }
            // 计算距离和费用
            double dist = CalculateDistance(start.Position, end.Position);
            long routeCost = (long)(dist * 5000);

            if (playerFunds >= routeCost)
            {
                playerFunds -= routeCost;
                TxtFunds.Text = $"💰 资金: {playerFunds:N0}";

                double startLat = start.Position.Lat;
                double startLng = start.Position.Lng;
                double endLat = end.Position.Lat;
                double endLng = end.Position.Lng;

                // ==========================================
                // 🌟 图形学引擎：二次贝塞尔曲线 (Quadratic Bezier Curve)
                // ==========================================
                List<PointLatLng> arcPoints = new List<PointLatLng>();

                // 1. 找中点
                double midLat = (startLat + endLat) / 2.0;
                double midLng = (startLng + endLng) / 2.0;

                // 2. 计算方向向量
                double dx = endLng - startLng;
                double dy = endLat - startLat;

                // 3. 计算控制点 (沿着法向量偏移)
                // 🌟 魔法在此：因为法向量的特性，A到B 和 B到A 会自动向着完全相反的方向弯曲！
                double curvature = 0.2; // 曲率：数值越大，弧线越鼓。0.2 是视觉最舒适的比例
                double ctrlLat = midLat + dx * curvature;
                double ctrlLng = midLng - dy * curvature;

                // 4. 生成曲线上的 20 个平滑过渡点
                int segments = 20;
                for (int i = 0; i <= segments; i++)
                {
                    double t = i / (double)segments;
                    double u = 1 - t;

                    // 核心数学公式：P = u^2*P0 + 2*u*t*P1 + t^2*P2
                    double pLat = (u * u * startLat) + (2 * u * t * ctrlLat) + (t * t * endLat);
                    double pLng = (u * u * startLng) + (2 * u * t * ctrlLng) + (t * t * endLng);

                    arcPoints.Add(new PointLatLng(pLat, pLng));
                }

                // ==========================================
                // 🎨 视觉表现：分配霓虹色，虚实结合
                // ==========================================
                Color[] neonColors = new Color[]
                {
                    Color.FromArgb(200, 0, 255, 255),   // 赛博青
                    Color.FromArgb(200, 255, 0, 255),   // 霓虹紫
                    Color.FromArgb(200, 255, 165, 0),   // 警告橙
                    Color.FromArgb(200, 50, 255, 50)    // 荧光绿
                };
                Color routeColor = neonColors[randomEngine.Next(neonColors.Length)];

                GMapRoute finalRoute = new GMapRoute(arcPoints) // 🌟 传入刚刚算好的 20 个曲线点！
                {
                    Shape = new System.Windows.Shapes.Path
                    {
                        Stroke = new SolidColorBrush(routeColor),
                        StrokeThickness = 2,
                        StrokeDashArray = new DoubleCollection { 4, 3 }, // 雷达虚线
                        Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = routeColor, BlurRadius = 12, ShadowDepth = 0 }
                    }
                };

                MainMap.Markers.Add(finalRoute);
                activeRoutes.Add(new GameRouteInfo { StartNode = start, EndNode = end, Distance = dist });

                TxtFunds.Foreground = Brushes.White;
            }
            else
            {
                MessageBox.Show($"余额不足！开通此航线需要 ¥{routeCost:N0}", "财务警报");
            }
        }
        // 🌟 按钮事件占位符：升级机场
        // 🌟 核心自救机制 1：扩建机场 (花 5000 万保命)
        private void UpgradeAirportBtn_Click(object sender, RoutedEventArgs e)
        {
            if (currentSelectedMarker == null) return;
            GameAirportInfo info = (currentSelectedMarker.Shape as FrameworkElement).Tag as GameAirportInfo;

            if (info.Level >= 3) { MessageBox.Show("机场已达最高级别 3！"); return; }

            long upgradeCost = info.Level == 1 ? 150000000 : 500000000; // 一级升二级1.5亿，二级升三级5亿

            if (playerFunds >= upgradeCost)
            {
                playerFunds -= upgradeCost;
                info.Level++;

                // 🌟 容量梯度：Lv2 容量翻 2.5 倍，Lv3 再翻 2.5 倍！
                info.MaxCapacity = (int)(info.MaxCapacity * 2.5);

                // 🌟🌟🌟 视觉反馈：让地图上的红点变大！！
                if (currentSelectedMarker.Shape is Canvas canvas)
                {
                    var dot = canvas.Children.OfType<System.Windows.Shapes.Ellipse>().FirstOrDefault();
                    if (dot != null)
                    {
                        dot.Width += 8; // 每次升级直径变大 8 像素
                        dot.Height += 8;
                        // 修正文字标签的排版和整个 Canvas 的居中偏移
                        currentSelectedMarker.Offset = new Point(-dot.Width / 2, -dot.Height / 2);
                        var textLabel = canvas.Children.OfType<TextBlock>().FirstOrDefault();
                        if (textLabel != null) { Canvas.SetLeft(textLabel, dot.Width + 4); Canvas.SetTop(textLabel, (dot.Height - 15) / 2); }
                    }
                }

                TxtFunds.Text = $"💰 资金: {playerFunds:N0}";
                RefreshPanelUI(info);
                // LogListBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] 🏗️ 机场升级！扩建为 Lv.{info.Level}，容量暴增至 {info.MaxCapacity:N0} 人！");
            }
        }

        // 🌟 核心投资机制 2：购买驻扎机队 (花 1.5 亿，运人的前置条件)
        private void BuyLocalPlaneBtn_Click(object sender, RoutedEventArgs e)
        {
            if (currentSelectedMarker == null) return;
            GameAirportInfo info = (currentSelectedMarker.Shape as FrameworkElement).Tag as GameAirportInfo;

            long planeCost = 150000000; // 买一架初始飞机 1.5 亿
            if (playerFunds >= planeCost)
            {
                playerFunds -= planeCost;
                // 🌟 新增一架实体飞机！
                string newPlaneId = "B-" + randomEngine.Next(1000, 9999);
                info.LocalFleet.Add(new GamePlaneInfo { Id = newPlaneId, Level = 1 });

                TxtFunds.Text = $"💰 资金: {playerFunds:N0}";
                RefreshPanelUI(info);
                // LogListBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] ✈️ 耗资 1.5 亿！全新客机【{newPlaneId}】已交付入驻！");
            }
            else
            {
                MessageBox.Show($"资金不足！购买一架飞机需要 ¥{planeCost:N0}", "财务警报");
            }
        }
        // 🌟 按钮事件占位符：购买航线
        private void DrawRouteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (currentSelectedMarker == null) return;

            // 检查起点是否有飞机
            var info = (currentSelectedMarker.Shape as FrameworkElement).Tag as GameAirportInfo;
            if (info.LocalFleet.Count <= 0)
            {
                MessageBox.Show("该机场没有驻扎飞机，无法开通航线！请先购买飞机。", "调度提醒");
                return;
            }

            isDrawingRoute = true;
            routeStartMarker = currentSelectedMarker;

            //LogListBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] 🔗 进入连线模式：请在地图上点击目标机场...");
            AirportInfoPanel.Visibility = Visibility.Collapsed; // 暂时关闭面板方便操作
        }
        // 🌟 核心交互：绘制跟随鼠标的“橡皮筋”航线预览虚线
        private void MainMap_MouseMove(object sender, MouseEventArgs e)
        {
            // 只有在“开通航线模式”且已经点选了起点的情况下，才触发拉线逻辑
            if (isDrawingRoute && routeStartMarker != null)
            {
                // 1. 获取当前鼠标在地图控件上的物理像素坐标
                Point mousePos = e.GetPosition(MainMap);

                // 2. 将物理像素坐标转换为地图的真实经纬度坐标
                PointLatLng mouseLatLng = MainMap.FromLocalToLatLng((int)mousePos.X, (int)mousePos.Y);

                // 3. 如果上一帧已经画了预览线，先把它从地图上擦除
                if (elasticLine != null)
                {
                    MainMap.Markers.Remove(elasticLine);
                }

                // 4. 创建一条全新的线，连接【起点机场】和【鼠标当前位置】
                List<PointLatLng> pts = new List<PointLatLng> { routeStartMarker.Position, mouseLatLng };

                elasticLine = new GMapRoute(pts)
                {
                    Shape = new System.Windows.Shapes.Path
                    {
                        Stroke = Brushes.Cyan,                             // 极客风的青色虚线
                        StrokeThickness = 2,                               // 线的粗细
                        StrokeDashArray = new DoubleCollection { 4, 2 },   // 虚线特效 (实线长4，空白长2)

                        // ==========================================
                        // 🌟🌟🌟 救命属性：物理穿透！让这根线变成幽灵！
                        // ==========================================
                        IsHitTestVisible = false
                    },
                    ZIndex = 500 // 保证虚线浮在底图上方，但尽量在机场红点的下方
                };

                // 5. 将这根最新的幽灵虚线添加到地图上
                MainMap.Markers.Add(elasticLine);

                // ==========================================
                // 🌟 新增：航线造价实时测算与悬浮提示
                // ==========================================
                // 使用 GMap 底层的专业 GIS 投影算法，计算两点之间的真实球面距离 (公里)
                double distanceKm = MainMap.MapProvider.Projection.GetDistance(routeStartMarker.Position, mouseLatLng);
                long previewCost = (long)(distanceKm * 50000); // 假定每公里造价 5 万元

                if (costPreviewMarker == null)
                {
                    TextBlock costTxt = new TextBlock
                    {
                        Background = new SolidColorBrush(Color.FromArgb(200, 10, 10, 20)),
                        Foreground = Brushes.Lime,
                        Padding = new Thickness(6),
                        FontWeight = FontWeights.Bold,
                        IsHitTestVisible = false // 也是幽灵，不挡点击
                    };
                    costPreviewMarker = new GMapMarker(mouseLatLng) { Shape = costTxt, Offset = new Point(20, 20), ZIndex = 1005 };
                    MainMap.Markers.Add(costPreviewMarker);
                }

                ((TextBlock)costPreviewMarker.Shape).Text = $"💰 预估建线费用: ¥ {previewCost:N0}\n📏 航程: {distanceKm:F1} km";
                costPreviewMarker.Position = mouseLatLng;
            }
        }
        // 🌟 终极结算引擎：处理破产定格、数据打包与画面弹窗


        // 🌟 重新接管按钮：一键重置宇宙
        private void RestartGameBtn_Click(object sender, RoutedEventArgs e)
        {
            // 直接调用我们写好的重开方法！极其优雅！
            LoadDataBtn_Click(null, null);
        }

        // 🌟 联机排行榜接口预留：打包 JSON 数据
        // 🌟 核心：载入本地存盘的排行榜数据
        private void LoadLeaderboard()
        {
            try
            {
                if (File.Exists(leaderboardFilePath))
                {
                    string json = File.ReadAllText(leaderboardFilePath);
                    leaderboardData = JsonSerializer.Deserialize<List<PlayerRecord>>(json) ?? new List<PlayerRecord>();
                }
            }
            catch { leaderboardData = new List<PlayerRecord>(); }
        }

        // 🌟 核心：提交战绩并展示排行榜
        // 🌟 终极进化：向全球中央服务器 (Spring Boot) 发送战报
        private async void SubmitScoreBtn_Click(object sender, RoutedEventArgs e)
        {
            int survivedDays = (gameVirtualTime - new DateTime(2026, 1, 1)).Days;
            string playerName = string.IsNullOrWhiteSpace(TxtPlayerName.Text) ? "神秘指挥官" : TxtPlayerName.Text;

            // 1. 打包数据（组装成对象准备上云）
            var record = new PlayerRecord
            {
                PlayerName = playerName,
                SurvivedDays = survivedDays,
                TransportedPax = totalTransportedPax,
                FinalFunds = playerFunds
            };
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            };
            // 将对象转为 JSON 文本格式
            string jsonPayload = System.Text.Json.JsonSerializer.Serialize(record, options);
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            try
            {
                // 🚀 核心：你的服务器收发地址！
                // 如果后端和游戏在同一台电脑上测试，写 http://127.0.0.1:9090/api/submit
                // 如果后端部署到了云服务器，换成真实的公网 IP，比如 http://114.55.66.77:9090/api/submit
                string serverUrl = "http://124.220.178.183:8084/api/submit";

                // 禁用按钮，防止玩家狂点导致服务器收到多份相同战报
                Button btn = sender as Button;
                if (btn != null)
                {
                    btn.IsEnabled = false;
                    btn.Content = "📡 正在连接全球卫星网络...";
                }

                // 2. 发送 POST 请求将数据砸向 Spring Boot
                using (var httpClient = new HttpClient())
                {
                    // 🚀 发射！
                    HttpResponseMessage response = await httpClient.PostAsync(serverUrl, content);

                    // 检查是否成功 (状态码 200 OK)
                    response.EnsureSuccessStatusCode();
                }

                // 3. 切换 UI 画面
                GameOverOverlay.Visibility = Visibility.Collapsed; // 关掉破产界面
                LeaderboardOverlay.Visibility = Visibility.Visible; // 弹排名人堂大屏

                // 4. 🌟 直接从云端拉取最新排行榜来显示！（这就用到了我们之前写的双榜单拉取方法）
                FetchCloudLeaderboard("survival");
            }
            catch (Exception ex)
            {
                // 如果服务器没开、端口不对、或者被防火墙拦截了，会弹这个框
                MessageBox.Show($"网络连接失败，战报未能上传：\n{ex.Message}", "卫星失联", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 恢复按钮状态（不管成功还是失败，都让按钮恢复可点）
                Button btn = sender as Button;
                if (btn != null)
                {
                    btn.IsEnabled = true;
                    btn.Content = "🏆 载入历史名人堂 (保存战绩)";
                }
            }
        }
        private async void FetchCloudLeaderboard(string endpoint)
        {
            try
            {
                // 你的服务器 IP 和 8080 端口
                string url = "http://124.220.178.183:8084/api/leaderboard/" + endpoint;

                using (var httpClient = new HttpClient())
                {
                    string json = await httpClient.GetStringAsync(url);

                    // 开启忽略大小写（因为 Java 传过来的可能是小写，C# 里是大写）
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var records = System.Text.Json.JsonSerializer.Deserialize<List<PlayerRecord>>(json, options);

                    // 刷新 UI
                    LeaderboardList.ItemsSource = null;
                    LeaderboardList.ItemsSource = records;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"拉取云端排行榜失败: {ex.Message}", "卫星失联");
            }
        }
        private void LoadSurvivalBoard_Click(object sender, RoutedEventArgs e)
        {
            FetchCloudLeaderboard("survival");
        }

        // 🔘 点击财富榜按钮
        private void LoadWealthBoard_Click(object sender, RoutedEventArgs e)
        {
            FetchCloudLeaderboard("wealth");
        }
        // 🌟 关掉排行榜，一键重开新局
        private void CloseLeaderboardBtn_Click(object sender, RoutedEventArgs e)
        {
            LeaderboardOverlay.Visibility = Visibility.Collapsed;
            //LoadDataBtn_Click(null, null); // 触发重开大招
        }
        // 🌟 用户自愿点击：上传详细报告至云端
        private async void UploadReportBtn_Click(object sender, RoutedEventArgs e)
        {
            if (currentSessionReport == null) return;

            // 补上玩家在输入框填写的代号
            currentSessionReport.PlayerName = string.IsNullOrWhiteSpace(TxtPlayerName.Text) ? "神秘指挥官" : TxtPlayerName.Text;
            currentSessionReport.TransportedPax = totalTransportedPax;
            var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
            string jsonPayload = System.Text.Json.JsonSerializer.Serialize(currentSessionReport, options);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                Button btn = sender as Button;
                if (btn != null) { btn.IsEnabled = false; btn.Content = "📡 数据上传中..."; }

                // 🚨 注意：这里我假定你会在 Java 里写一个新的接收详细报告的接口 /api/report/upload
                string serverUrl = "http://124.220.178.183:8084/api/report/upload";

                using (var httpClient = new HttpClient())
                {
                    HttpResponseMessage response = await httpClient.PostAsync(serverUrl, content);
                    response.EnsureSuccessStatusCode();
                }

                MessageBox.Show("✅ 详细营运数据已成功上传至云端！", "上传成功", MessageBoxButton.OK, MessageBoxImage.Information);
                if (btn != null) btn.Content = "☁️ 已上传云端";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"上传失败：\n{ex.Message}", "网络错误", MessageBoxButton.OK, MessageBoxImage.Error);
                if (sender is Button btn) { btn.IsEnabled = true; btn.Content = "📤 重新上传详细数据"; }
            }
        }

        // 🌟 点击打开个人档案
        private async void BtnPersonalRecord_Click(object sender, RoutedEventArgs e)
        {
            string playerName = TxtPlayerName.Text;
            if (string.IsNullOrWhiteSpace(playerName) || playerName == "AOC-001")
            {
                MessageBox.Show("未检测到指挥官身份，请先登录系统！", "访问拒绝", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnPersonalRecord.Content = "📡 正在连线总部档案库...";
            BtnPersonalRecord.IsEnabled = false;

            try
            {
                using (var httpClient = new HttpClient())
                {
                    // 🚨 注意换成你的公网 IP
                    string url = $"http://124.220.178.183:8084/api/report/history?playerName={Uri.EscapeDataString(playerName)}";
                    string json = await httpClient.GetStringAsync(url);

                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var historyList = System.Text.Json.JsonSerializer.Deserialize<List<GameReportPayload>>(json, options);

                    if (historyList != null)
                    {
                        // 1. 标题更新
                        TxtHistoryTitle.Text = $"🧑‍✈️ 指挥官 {playerName} 的服役档案";

                        // 2. 疯狂计算汇总数据！
                        TxtTotalGames.Text = historyList.Count.ToString();
                        TxtTotalDays.Text = historyList.Sum(x => x.SurvivedDays).ToString();
                        TxtTotalPax.Text = historyList.Sum(x => x.TransportedPax).ToString("N0");
                        TxtTotalWealth.Text = "¥ " + historyList.Sum(x => x.FinalFunds).ToString("N0");

                        // 3. 绑定列表
                        PersonalHistoryList.ItemsSource = null;
                        PersonalHistoryList.ItemsSource = historyList;

                        // 4. 弹出大屏！
                        PersonalHistoryOverlay.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"获取档案失败，请检查网络：\n{ex.Message}", "卫星失联", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnPersonalRecord.Content = "📊 我的专属服役档案";
                BtnPersonalRecord.IsEnabled = true;
            }
        }

        // 🌟 关闭档案面板
        private void ClosePersonalHistoryBtn_Click(object sender, RoutedEventArgs e)
        {
            PersonalHistoryOverlay.Visibility = Visibility.Collapsed;
        }
        // 🌟 用户自愿点击：从云端下载自己的数据并生成 CSV
        private async void DownloadReportBtn_Click(object sender, RoutedEventArgs e)
        {
            string playerName = string.IsNullOrWhiteSpace(TxtPlayerName.Text) ? "神秘指挥官" : TxtPlayerName.Text;

            try
            {
                // 🚨 注意：假定 Java 有个接口可以通过玩家名字拉取他的最新数据
                string url = $"http://124.220.178.183:8084/api/report/download?playerName={Uri.EscapeDataString(playerName)}";

                using (var httpClient = new HttpClient())
                {
                    string json = await httpClient.GetStringAsync(url);
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var report = System.Text.Json.JsonSerializer.Deserialize<GameReportPayload>(json, options);

                    if (report == null || report.AirportStats == null) return;

                    // 转换为 CSV 并保存到桌面
                    StringBuilder csv = new StringBuilder();
                    csv.AppendLine("机场名称,ICAO代码,最终等级,滞留旅客,最大容量,累计净利润(元),驻扎机队(架),每日客流基数");
                    foreach (var stat in report.AirportStats)
                    {
                        csv.AppendLine($"{stat.AirportName},{stat.Icao},{stat.FinalLevel},{stat.StrandedPax},{stat.MaxCapacity},{stat.TotalProfit},{stat.FleetSize},{stat.DailyPaxRate}");
                    }

                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    string filePath = System.IO.Path.Combine(desktopPath, $"CloudReport_{playerName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                    File.WriteAllText(filePath, csv.ToString(), new UTF8Encoding(true));

                    MessageBox.Show($"✅ 云端数据已成功拉取！\n已为您导出为 Excel (CSV) 格式，保存在：\n{filePath}", "下载成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"拉取云端数据失败：\n{ex.Message}\n请确认您之前是否成功上传过数据。", "下载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        // 🌟 主界面随时点击查看全球排行榜
        private void BtnGlobalLeaderboard_Click(object sender, RoutedEventArgs e)
        {
            // 默认拉取一下“生存专家榜”的数据并刷新 UI
            FetchCloudLeaderboard("survival");

            // 弹出金色大屏
            LeaderboardOverlay.Visibility = Visibility.Visible;
        }
        // 隐藏面板
        private void ClosePanel_Click(object sender, RoutedEventArgs e)
        {
            AirportInfoPanel.Visibility = Visibility.Collapsed;
        }
    }
}