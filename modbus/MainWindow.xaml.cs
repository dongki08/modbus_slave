using Modbus.Device;
using Modbus.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace modbus
{
    static class DiscriminatedUnionHelper
    {
        // union: DiscriminatedUnion<ReadOnlyCollection<bool>, ReadOnlyCollection<ushort>>
        public static void SwitchBoolOrUshort(object union,
            Action<ReadOnlyCollection<bool>> onBool,
            Action<ReadOnlyCollection<ushort>> onUshort)
        {
            if (union == null) return;

            var t = union.GetType();

            // 1) Switch(Action<A>, Action<B>) 메서드가 있으면 그걸 호출
            var m = t.GetMethod("Switch", BindingFlags.Public | BindingFlags.Instance);
            if (m != null)
            {
                var pars = m.GetParameters();
                if (pars.Length == 2)
                {
                    m.Invoke(union, new object[] { onBool, onUshort });
                    return;
                }
            }

            // 2) IsA/A/IsB/B 또는 CaseA/CaseB 프로퍼티 패턴 처리
            bool? isA = t.GetProperty("IsA")?.GetValue(union) as bool? ??
                        t.GetProperty("IsFirst")?.GetValue(union) as bool?;

            if (isA == true)
            {
                var aProp = t.GetProperty("A") ?? t.GetProperty("CaseA") ?? t.GetProperty("First");
                var aVal = aProp?.GetValue(union) as ReadOnlyCollection<bool>;
                onBool?.Invoke(aVal ?? new ReadOnlyCollection<bool>(new List<bool>()));
            }
            else
            {
                var bProp = t.GetProperty("B") ?? t.GetProperty("CaseB") ?? t.GetProperty("Second");
                var bVal = bProp?.GetValue(union) as ReadOnlyCollection<ushort>;
                onUshort?.Invoke(bVal ?? new ReadOnlyCollection<ushort>(new List<ushort>()));
            }
        }
    }
    
    public partial class MainWindow : Window
    {
        private TcpListener _listener;
        private ModbusTcpSlave _slave;                 // 단일 DataStore 방식 (UnitId=0, 모든 Unit 수신)
        // NOTE: 필요 시 ModbusFactory + SlaveNetwork 구조로 전환 가능 (UnitID별 슬레이브 개별 생성) — 주석 참고

        private readonly System.Threading.AsyncLocal<byte?> _currentUnitId = new System.Threading.AsyncLocal<byte?>();
        private UnifiedDataStore _dataStore;           // 4타입 통합 DataStore + 장치 캐시
        private readonly Dictionary<byte, ModbusSlaveDevice> _devices = new Dictionary<byte, ModbusSlaveDevice>();

        private bool _running = false;
        private CancellationTokenSource _cts;

        private readonly DispatcherTimer _uiTimer;
        private volatile bool _pendingUIUpdate;
        private readonly object _uiLock = new object();

        public MainWindow()
        {
            InitializeComponent();

            _dataStore = new UnifiedDataStore();
            StartAddressTextBox.ToolTip = "시작 주소 오프셋 (0부터 시작)\n예: 0 입력 시 30001/40001/00001/10001 기준의 첫 칸";

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps
            _uiTimer.Tick += (s, e) => { if (_pendingUIUpdate) FlushUiUpdate(); };
            _uiTimer.Start();
        }

        #region 타이틀바
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) Maximize_Click(null, null);
            else DragMove();
        }
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        #endregion

        #region 서버 제어
        private async void StartServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_running)
                {
                    ShowBox("서버가 이미 실행 중입니다.", "정보", MessageBoxImage.Information);
                    return;
                }

                if (_devices.Count == 0)
                {
                    ShowBox("장치(Unit ID)를 최소 1개 이상 추가한 뒤 시작하세요.", "안내", MessageBoxImage.Warning);
                    return;
                }

                var ip = IPAddress.Parse(IpTextBox.Text);
                var port = int.Parse(PortTextBox.Text);

                _listener = new TcpListener(ip, port);
                _listener.Start();

                _slave = ModbusTcpSlave.CreateTcp(0, _listener); // UnitId=0 : 모든 UnitID 수신 (단일 DataStore)
                _slave.DataStore = _dataStore;

                // 요청 수신 로깅/디바이스 범위 로딩
                _slave.ModbusSlaveRequestReceived += (s, args) =>
                {
                    try
                    {
                        var unitId = args.Message.SlaveAddress;
                        var fc = args.Message.FunctionCode;
                        var fcName = GetFunctionCodeName(fc);

                        // 장치 존재 확인
                        if (!_devices.ContainsKey(unitId))
                        {
                            Log($"⚠️ [UNKNOWN UNIT] u={unitId} fc={fc}({fcName}) - 미등록 Unit ID 요청");
                            // 여기서 Exception 응답을 강제하려면 ModbusFactory + SlaveNetwork 구조 사용 권장 (주석 참고)
                        }
                        else
                        {
                            // 요청 직전 장치 캐시 → DataStore 로드
                            _dataStore.LoadFromDeviceCache(unitId, _devices[unitId]);
                        }

                        Log($"🔔 요청 - [FC{fc:00}] {fcName} u={unitId}");
                    }
                    catch (Exception ex)
                    {
                        Log($"❌ 요청 처리 예외: {ex.Message}");
                    }
                };

                // 읽기/쓰기 이벤트 -> FC형 로그
                _dataStore.DataStoreReadFrom += OnDataStoreRead;
                _dataStore.DataStoreWrittenTo += OnDataStoreWrite;

                _running = true;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ListenLoop(_cts.Token));

                ServerStatusText.Text = "🟢 서버 실행중";
                Log($"🚀 서버 시작: {ip}:{port}");
                ShowBox("서버가 성공적으로 시작되었습니다!", "성공", MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowBox($"서버 시작 실패: {ex.Message}", "오류", MessageBoxImage.Error);
            }
        }

        private void StopServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _running = false;
                _cts?.Cancel();

                _slave?.Dispose();
                _slave = null;

                _listener?.Stop();
                _listener = null;

                ServerStatusText.Text = "🔴 서버 중지됨";
                Log("⏹ 서버 중지됨");
                ShowBox("서버가 중지되었습니다.", "정보", MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowBox($"서버 중지 실패: {ex.Message}", "오류", MessageBoxImage.Error);
            }
        }

        private async Task ListenLoop(CancellationToken token)
        {
            try
            {
                await Task.Run(() =>
                {
                    while (!token.IsCancellationRequested && _running)
                    {
                        try
                        {
                            _slave?.Listen();
                            Thread.Sleep(1);
                        }
                        catch (Exception ex)
                        {
                            if (!token.IsCancellationRequested)
                                Dispatcher.BeginInvoke(new Action(() => Log($"❌ Listen 오류: {ex.Message}")));
                            Thread.Sleep(50);
                        }
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.BeginInvoke(new Action(() => Log("🔄 서버 종료됨")));
            }
        }
        #endregion

        #region 장치 추가/삭제 & UI
        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            byte unitId;
            int start, count;

            if (!byte.TryParse(UnitIdTextBox.Text, out unitId))
            {
                ShowBox("Unit ID를 0~255로 입력하세요.", "입력 오류", MessageBoxImage.Warning);
                return;
            }
            if (_devices.ContainsKey(unitId))
            {
                ShowBox("이미 존재하는 Unit ID 입니다.", "중복 오류", MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(StartAddressTextBox.Text, out start) || start < 0)
            {
                ShowBox("시작 주소(오프셋)는 0 이상의 정수여야 합니다.", "입력 오류", MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(AddressCountTextBox.Text, out count) || count <= 0)
            {
                ShowBox("개수는 1 이상의 정수여야 합니다.", "입력 오류", MessageBoxImage.Warning);
                return;
            }

            // 한 번에 4타입 생성
            var device = new ModbusSlaveDevice(unitId);
            device.InitializeCoils(start, count);               // 00001+
            device.InitializeDiscreteInputs(start, count);      // 10001+
            device.InitializeHoldingRegisters(start, count);    // 40001+
            device.InitializeInputRegisters(start, count);      // 30001+

            _devices.Add(unitId, device);
            _dataStore.AddDevice(unitId, device);

            var tab = new TabItem { Header = $"장치 {unitId}", Tag = unitId };
            tab.Content = CreateDeviceContent(device);
            DeviceTabControl.Items.Add(tab);
            DeviceTabControl.SelectedItem = tab;

            Log($"➕ 장치 추가: u={unitId}, start={start}, count={count} (01/02/03/04 모두 생성)");
        }

        private void DeleteDevice_Click(object sender, RoutedEventArgs e)
        {
            var tab = DeviceTabControl.SelectedItem as TabItem;
            if (tab == null || !(tab.Tag is byte))
            {
                ShowBox("삭제할 장치를 선택하세요.", "선택 오류", MessageBoxImage.Warning);
                return;
            }
            var unitId = (byte)tab.Tag;
            if (MessageBox.Show($"장치 {unitId}를 삭제하시겠습니까?", "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                DeviceTabControl.Items.Remove(tab);
                _devices.Remove(unitId);
                _dataStore.RemoveDevice(unitId);
                Log($"🗑️ 장치 삭제: u={unitId}");
            }
        }

        private UIElement CreateDeviceContent(ModbusSlaveDevice device)
        {
            var grid = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 상단 설명
            var header = new TextBlock
            {
                Text = $"Unit ID {device.UnitId} • 01/02/03/04 전체 표시",
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            grid.Children.Add(header);

            // 2x2 카드 레이아웃
            var cards = new Grid();
            cards.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cards.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cards, 1);
            grid.Children.Add(cards);

            // Coils
            if (device.Coils != null)
            {
                var card = CreateCoilCard("🔵 Coils [00001+]", device.Coils);
                Grid.SetRow(card, 0); Grid.SetColumn(card, 0);
                cards.Children.Add(card);
            }
            // Discrete Inputs
            if (device.DiscreteInputs != null)
            {
                var card = CreateCoilCard("🟢 Discrete Inputs [10001+]", device.DiscreteInputs);
                Grid.SetRow(card, 0); Grid.SetColumn(card, 1);
                cards.Children.Add(card);
            }
            // Holding
            if (device.HoldingRegisters != null)
            {
                var card = CreateOptimizedRegisterCard("🟠 Holding Registers [40001+]", device.HoldingRegisters);
                Grid.SetRow(card, 1); Grid.SetColumn(card, 0);
                cards.Children.Add(card);
            }
            // Input
            if (device.InputRegisters != null)
            {
                var card = CreateOptimizedRegisterCard("🟡 Input Registers [30001+]", device.InputRegisters);
                Grid.SetRow(card, 1); Grid.SetColumn(card, 1);
                cards.Children.Add(card);
            }

            return grid;
        }

        private void ShowDeviceData_Click(object sender, RoutedEventArgs e)
        {
            DeviceDataButton.Style = (Style)FindResource("ActiveToggleButton");
            LogButton.Style = (Style)FindResource("ToggleButton2");
            DeviceTabControl.Visibility = Visibility.Visible;
            LogContainer.Visibility = Visibility.Collapsed;
            DeleteDeviceButton.Visibility = Visibility.Visible;
        }

        private void ShowLog_Click(object sender, RoutedEventArgs e)
        {
            DeviceDataButton.Style = (Style)FindResource("ToggleButton2");
            LogButton.Style = (Style)FindResource("ActiveToggleButton");
            DeviceTabControl.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Visible;
            DeleteDeviceButton.Visibility = Visibility.Collapsed;
        }
        #endregion

        #region UI 카드들 + 에디터
        private UIElement CreateOptimizedRegisterCard(string title, ObservableCollection<DualRegisterModel> data)
        {
            var container = new Grid { Margin = new Thickness(8, 8, 8, 8) };

            var border = new Border
            {
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x2D, 0x2D, 0x30),
                    Color.FromRgb(0x25, 0x25, 0x26),
                    90),
                CornerRadius = new CornerRadius(10),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Effect = (Effect)FindResource("CardShadow")
            };
            container.Children.Add(border);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 8),
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x00, 0x78, 0xD4),
                    BlurRadius = 8, ShadowDepth = 0, Opacity = 0.4
                }
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var list = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = data
            };

            // 가상화
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            VirtualizingStackPanel.SetIsVirtualizing(list, true);
            VirtualizingStackPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(list, true);

            // 아이템 -> ModernRegisterControl
            var template = new DataTemplate();
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new LinearGradientBrush(
                Color.FromRgb(0x3F, 0x3F, 0x46), Color.FromRgb(0x32, 0x32, 0x37), 90));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(8));
            borderFactory.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 8));
            borderFactory.SetValue(Border.BorderBrushProperty, (Brush)FindResource("BorderBrush"));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.EffectProperty, FindResource("SoftShadow"));

            var controlFactory = new FrameworkElementFactory(typeof(ModernRegisterControl));
            controlFactory.SetBinding(ModernRegisterControl.RegisterModelProperty, new System.Windows.Data.Binding("."));
            controlFactory.AddHandler(ModernRegisterControl.RegisterValueUpdatedEvent, new RoutedEventHandler(RegisterValue_FromEditor));
            borderFactory.AppendChild(controlFactory);

            template.VisualTree = borderFactory;
            list.ItemTemplate = template;

            // ListBoxItem 컨테이너 투명화
            var itemStyle = new Style(typeof(ListBoxItem));
            var noTmpl = new ControlTemplate(typeof(ListBoxItem));
            noTmpl.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
            itemStyle.Setters.Add(new Setter(ListBoxItem.TemplateProperty, noTmpl));
            itemStyle.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(ListBoxItem.FocusableProperty, false));
            list.ItemContainerStyle = itemStyle;

            Grid.SetRow(list, 1);
            grid.Children.Add(list);

            border.Child = grid;
            return container;
        }

        private UIElement CreateCoilCard(string title, ObservableCollection<RegisterModel> data)
        {
            var container = new Grid { Margin = new Thickness(8, 8, 8, 8) };
            var border = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(0x2D, 0x2D, 0x30), Color.FromRgb(0x25, 0x25, 0x26), 90),
                CornerRadius = new CornerRadius(10),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Effect = (Effect)FindResource("CardShadow")
            };
            container.Children.Add(border);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 6),
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x00, 0x78, 0xD4),
                    BlurRadius = 8, ShadowDepth = 0, Opacity = 0.4
                }
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var dg = new DataGrid
            {
                ItemsSource = data,
                IsReadOnly = false,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeColumns = false,
                CanUserResizeRows = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            dg.EnableRowVirtualization = true;
            dg.EnableColumnVirtualization = true;
            VirtualizingStackPanel.SetIsVirtualizing(dg, true);
            VirtualizingStackPanel.SetVirtualizationMode(dg, VirtualizationMode.Recycling);

            // 헤더 스타일
            dg.ColumnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            dg.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, FindResource("SurfaceBrush")));
            dg.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, FindResource("TextBrush")));
            dg.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.Bold));
            dg.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 13.0));
            dg.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.HeightProperty, 35.0));

            dg.Columns.Add(new DataGridTextColumn
            {
                Header = "Address",
                Binding = new System.Windows.Data.Binding("DisplayAddress"),
                IsReadOnly = true,
                Width = new DataGridLength(0.45, DataGridLengthUnitType.Star),
                MinWidth = 100
            });
            dg.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Binding = new System.Windows.Data.Binding("Value"),
                IsReadOnly = false,
                Width = new DataGridLength(0.55, DataGridLengthUnitType.Star),
                MinWidth = 80
            });

            dg.CellEditEnding += (s, e) =>
            {
                if (e.EditAction == DataGridEditAction.Commit)
                {
                    var reg = e.Row.Item as RegisterModel;
                    if (reg != null)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Log($"📝 Coil/Input {reg.DisplayAddress} => {reg.Value}");
                            PushUiChangeToCurrentDevice();
                        }), DispatcherPriority.Background);
                    }
                }
            };

            Grid.SetRow(dg, 1);
            grid.Children.Add(dg);

            border.Child = grid;
            return container;
        }

        private void RegisterValue_FromEditor(object sender, RoutedEventArgs e)
        {
            // ModernRegisterControl 값 변경 버블링
            PushUiChangeToCurrentDevice();
        }

        private void PushUiChangeToCurrentDevice()
        {
            if (_dataStore.IsUpdatingFromMaster) return;

            var tab = DeviceTabControl.SelectedItem as TabItem;
            if (tab != null && tab.Tag is byte)
            {
                var unitId = (byte)tab.Tag;
                if (_devices.ContainsKey(unitId))
                {
                    var device = _devices[unitId];
                    _dataStore.UpdateDeviceCache(unitId, device);
                    System.Diagnostics.Debug.WriteLine($"UI->캐시 반영 완료 (u={unitId})");
                }
            }
        }
        #endregion

        #region DataStore 이벤트 -> FC 로그
        private void OnDataStoreRead(object sender, DataStoreEventArgs e)
        {
            ushort qty = 0;

            DiscriminatedUnionHelper.SwitchBoolOrUshort(
                e.Data,
                bools => { if (bools != null) qty = (ushort)bools.Count; },
                regs  => { if (regs  != null) qty = (ushort)regs.Count; }
            );

            var fc   = GuessReadFunctionCode(e.ModbusDataType);
            var unit = _currentUnitId.Value ?? 0;

            Log($"📖 [FC{fc:00}] READ u={unit} type={e.ModbusDataType} addr=0x{e.StartAddress:X4}({e.StartAddress}) qty={qty}");
        }


        private void OnDataStoreWrite(object sender, DataStoreEventArgs e)
        {
            ushort qty = 0;
            string payload = "";

            DiscriminatedUnionHelper.SwitchBoolOrUshort(
                e.Data,
                bools =>
                {
                    if (bools != null)
                    {
                        qty = (ushort)bools.Count;
                        if (bools.Count > 0)
                            payload = $" values=[{string.Join(",", bools.Take(16).Select(b => b ? 1 : 0))}{(bools.Count > 16 ? ",..." : "")}]";
                    }
                },
                regs =>
                {
                    if (regs != null)
                    {
                        qty = (ushort)regs.Count;
                        if (regs.Count > 0)
                            payload = $" values=[{string.Join(",", regs.Take(8))}{(regs.Count > 8 ? ",..." : "")}]";
                    }
                }
            );

            byte fc = 0;
            if (e.ModbusDataType == ModbusDataType.Coil)                fc = (byte)(qty == 1 ? 5 : 15);
            else if (e.ModbusDataType == ModbusDataType.HoldingRegister) fc = (byte)(qty == 1 ? 6 : 16);

            var unit = _currentUnitId.Value ?? 0;

            if (fc != 0)
                Log($"✍️ [FC{fc:00}] WRITE u={unit} type={e.ModbusDataType} addr=0x{e.StartAddress:X4}({e.StartAddress}) qty={qty}{payload}");

            // UI 반영 (기존 그대로)
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var tab = DeviceTabControl.SelectedItem as TabItem;
                        if (tab != null && tab.Tag is byte currentUnit && _devices.ContainsKey(currentUnit))
                        {
                            _dataStore.UpdateDeviceFromDataStoreToDevice(e, _devices[currentUnit]);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"UI 동기화 오류: {ex.Message}");
                    }
                }), DispatcherPriority.Background);
            }
            catch { }
        }


        private static byte GuessReadFunctionCode(ModbusDataType type)
        {
            switch (type)
            {
                case ModbusDataType.Coil: return 1;
                case ModbusDataType.Input: return 2;
                case ModbusDataType.HoldingRegister: return 3;
                case ModbusDataType.InputRegister: return 4;
                default: return 0;
            }
        }

        private static string GetFunctionCodeName(byte fc)
        {
            switch (fc)
            {
                case 1: return "Read Coils";
                case 2: return "Read Discrete Inputs";
                case 3: return "Read Holding Registers";
                case 4: return "Read Input Registers";
                case 5: return "Write Single Coil";
                case 6: return "Write Single Register";
                case 15: return "Write Multiple Coils";
                case 16: return "Write Multiple Registers";
                default: return $"Unknown FC {fc}";
            }
        }
        #endregion

        #region 공통
        private void FlushUiUpdate()
        {
            lock (_uiLock)
            {
                if (_pendingUIUpdate) _pendingUIUpdate = false;
            }
        }

        private void Log(string msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.Items.Add($"⏰ {DateTime.Now:HH:mm:ss} - {msg}");
                if (LogBox.Items.Count > 100) LogBox.Items.RemoveAt(0);
                LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
            }), DispatcherPriority.Background);
        }

        private MessageBoxResult ShowBox(string message, string title, MessageBoxImage icon)
            => MessageBox.Show(message, title, MessageBoxButton.OK, icon);

        protected override void OnClosing(CancelEventArgs e)
        {
            _uiTimer?.Stop();
            if (_running) StopServer_Click(null, null);
            base.OnClosing(e);
        }
        #endregion
    }

    #region 모델 & 컨트롤
    public class RegisterModel : INotifyPropertyChanged
    {
        private int _value;
        public int DisplayAddress { get; set; }
        public int ModbusAddress { get; set; } // 0-based offset

        public int Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                }
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class DualRegisterModel : INotifyPropertyChanged
    {
        private int _value;
        public int DisplayAddress { get; set; } // 30001+/40001+
        public int ModbusAddress { get; set; }  // 0-based offset

        public int RegisterValue
        {
            get => _value;
            set
            {
                var v = Math.Max(0, Math.Min(65535, value));
                if (_value != v)
                {
                    _value = v;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RegisterValue)));
                }
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class ModernRegisterControl : UserControl
    {
        public static readonly DependencyProperty RegisterModelProperty =
            DependencyProperty.Register("RegisterModel", typeof(DualRegisterModel), typeof(ModernRegisterControl),
                new PropertyMetadata(null, OnModelChanged));

        public DualRegisterModel RegisterModel
        {
            get { return (DualRegisterModel)GetValue(RegisterModelProperty); }
            set { SetValue(RegisterModelProperty, value); }
        }

        public static readonly RoutedEvent RegisterValueUpdatedEvent = EventManager.RegisterRoutedEvent(
            "RegisterValueUpdated", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ModernRegisterControl));

        public event RoutedEventHandler RegisterValueUpdated
        {
            add { AddHandler(RegisterValueUpdatedEvent, value); }
            remove { RemoveHandler(RegisterValueUpdatedEvent, value); }
        }

        private TextBox _decBox, _hexBox, _strBox, _binBox;
        private Grid _bitGrid;
        private TextBlock _header;
        private bool _internal;

        public ModernRegisterControl()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _header = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                Margin = new Thickness(0, 0, 0, 12),
                Effect = new DropShadowEffect { Color = Color.FromRgb(0x00, 0x78, 0xD4), BlurRadius = 5, ShadowDepth = 0, Opacity = 0.6 }
            };
            Grid.SetRow(_header, 0);
            root.Children.Add(_header);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetRow(row, 1);
            root.Children.Add(row);

            // 10진
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 0) };
                var lbl = new TextBlock { Text = "10진수", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, FontWeight = FontWeights.Medium };
                _decBox = BuildTextBox(60);
                _decBox.LostFocus += (s, e) => DecChanged();
                _decBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { DecChanged(); e.Handled = true; } };
                p.Children.Add(lbl); p.Children.Add(_decBox); row.Children.Add(p);
            }
            // 16진
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 0) };
                var lbl = new TextBlock { Text = "16진수", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, FontWeight = FontWeights.Medium };
                _hexBox = BuildTextBox(70);
                _hexBox.LostFocus += (s, e) => HexChanged();
                _hexBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { HexChanged(); e.Handled = true; } };
                p.Children.Add(lbl); p.Children.Add(_hexBox); row.Children.Add(p);
            }
            // 문자열
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 0) };
                var lbl = new TextBlock { Text = "문자열", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, FontWeight = FontWeights.Medium };
                _strBox = BuildTextBox(45); _strBox.MaxLength = 2;
                _strBox.LostFocus += (s, e) => StrChanged();
                _strBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { StrChanged(); e.Handled = true; } };
                p.Children.Add(lbl); p.Children.Add(_strBox); row.Children.Add(p);
            }
            // 2진 보기
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 0) };
                var lbl = new TextBlock { Text = "2진수", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, FontWeight = FontWeights.Medium };
                _binBox = BuildTextBox(140); _binBox.IsReadOnly = true;
                _binBox.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
                _binBox.FontFamily = new FontFamily("Consolas, Monaco, monospace");
                _binBox.FontSize = 11;
                _binBox.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
                p.Children.Add(lbl); p.Children.Add(_binBox); row.Children.Add(p);
            }
            // 비트 에디터
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal };
                var lbl = new TextBlock { Text = "비트", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, FontWeight = FontWeights.Medium };
                _bitGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
                _bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int i = 0; i < 16; i++) _bitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                for (int bit = 15; bit >= 0; bit--)
                {
                    var l = new TextBlock { Text = bit.ToString(), FontSize = 8, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(1, 0, 1, 2) };
                    Grid.SetRow(l, 0); Grid.SetColumn(l, 15 - bit); _bitGrid.Children.Add(l);
                }
                for (int bit = 15; bit >= 0; bit--)
                {
                    var tb = BuildBitBox(); tb.Tag = bit;
                    tb.TextChanged += BitChanged; tb.MouseDoubleClick += (s, e) => { var t = (TextBox)s; t.Text = (t.Text == "1") ? "0" : "1"; };
                    tb.KeyDown += (s, e) =>
                    {
                        if (e.Key == Key.D0 || e.Key == Key.NumPad0) { tb.Text = "0"; e.Handled = true; }
                        else if (e.Key == Key.D1 || e.Key == Key.NumPad1) { tb.Text = "1"; e.Handled = true; }
                        else if (e.Key == Key.Left || e.Key == Key.Right)
                        {
                            int pos = (int)tb.Tag;
                            int target = (e.Key == Key.Left) ? pos - 1 : pos + 1;
                            if (target >= 0 && target <= 15)
                            {
                                var next = _bitGrid.Children.OfType<TextBox>().FirstOrDefault(x => (int)x.Tag == target);
                                next?.Focus(); next?.SelectAll();
                            }
                            e.Handled = true;
                        }
                        else e.Handled = true;
                    };
                    Grid.SetRow(tb, 1); Grid.SetColumn(tb, 15 - bit); _bitGrid.Children.Add(tb);
                }
                p.Children.Add(lbl); p.Children.Add(_bitGrid); row.Children.Add(p);
            }

            Content = root;
        }

        private TextBox BuildTextBox(double width)
        {
            var tb = new TextBox { Width = width, Height = 28, Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)), Foreground = new SolidColorBrush(Colors.White), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)), BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 0, 0), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
            // 둥근 템플릿
            var tmpl = new ControlTemplate(typeof(TextBox));
            var b = new FrameworkElementFactory(typeof(Border));
            b.Name = "border";
            b.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            b.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
            b.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer));
            sv.Name = "PART_ContentHost";
            sv.SetValue(ScrollViewer.FocusableProperty, false);
            sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            sv.SetValue(ScrollViewer.MarginProperty, new TemplateBindingExtension(TextBox.PaddingProperty));
            b.AppendChild(sv);
            tmpl.VisualTree = b;
            var trg = new Trigger { Property = TextBox.IsFocusedProperty, Value = true };
            trg.Setters.Add(new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), "border"));
            trg.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(2), "border"));
            trg.Setters.Add(new Setter(TextBox.EffectProperty, new DropShadowEffect { Color = Color.FromRgb(0x00, 0x78, 0xD4), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6 }, "border"));
            tmpl.Triggers.Add(trg);
            tb.Template = tmpl;
            return tb;
        }

        private TextBox BuildBitBox()
        {
            var tb = new TextBox
            {
                Width = 20,
                Height = 28,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MaxLength = 1,
                IsTabStop = false,
                Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                BorderThickness = new Thickness(1)
            };
            var tmpl = new ControlTemplate(typeof(TextBox));
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            b.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
            b.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer));
            sv.Name = "PART_ContentHost";
            sv.SetValue(ScrollViewer.FocusableProperty, false);
            sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            b.AppendChild(sv);
            tmpl.VisualTree = b;
            tb.Template = tmpl;
            return tb;
        }

        private void BitChanged(object sender, RoutedEventArgs e)
        {
            if (_internal || RegisterModel == null) return;
            var tb = (TextBox)sender;
            if (tb.Text == "0" || tb.Text == "1")
            {
                var bit = (int)tb.Tag;
                var newBit = tb.Text == "1" ? 1 : 0;
                // 반영
                var bits = GetCurrentBits();
                bits[bit] = newBit == 1;
                var newValue = BitsToUShort(bits);
                if (RegisterModel.RegisterValue != newValue)
                {
                    RegisterModel.RegisterValue = newValue;
                    RaiseEvent(new RoutedEventArgs(RegisterValueUpdatedEvent));
                }
                UpdateBitAppearance(tb, newBit);
                UpdateDisplays();
            }
            else
            {
                // 잘못 입력 -> 원복
                var bit = (int)tb.Tag;
                var val = (RegisterModel.RegisterValue >> bit) & 1;
                tb.Text = val.ToString();
                UpdateBitAppearance(tb, val);
            }
        }

        private void UpdateBitAppearance(TextBox tb, int bit)
        {
            if (bit == 1)
            {
                tb.Background = new LinearGradientBrush(Color.FromRgb(0x39, 0xFF, 0x14), Color.FromRgb(0x00, 0xFF, 0x41), 90);
                tb.Foreground = new SolidColorBrush(Colors.Black);
                tb.Effect = new DropShadowEffect { Color = Color.FromRgb(0x00, 0xFF, 0x41), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.8 };
            }
            else
            {
                tb.Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
                tb.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
                tb.Effect = null;
            }
        }

        private bool[] GetCurrentBits()
        {
            var arr = new bool[16];
            for (int i = 0; i < 16; i++)
            {
                int bit = i;
                int v = (RegisterModel.RegisterValue >> bit) & 1;
                arr[bit] = v == 1;
            }
            return arr;
        }

        private static ushort BitsToUShort(bool[] bits)
        {
            int val = 0;
            for (int i = 0; i < 16; i++) if (bits[i]) val |= (1 << i);
            return (ushort)val;
        }

        private void DecChanged()
        {
            if (_internal || RegisterModel == null) return;
            int v;
            if (int.TryParse(_decBox.Text, out v))
            {
                v = Math.Max(0, Math.Min(65535, v));
                if (v != RegisterModel.RegisterValue)
                {
                    RegisterModel.RegisterValue = v;
                    RaiseEvent(new RoutedEventArgs(RegisterValueUpdatedEvent));
                }
            }
            else _decBox.Text = RegisterModel.RegisterValue.ToString();
            UpdateDisplays();
        }

        private void HexChanged()
        {
            if (_internal || RegisterModel == null) return;
            var txt = _hexBox.Text.Trim().Replace("0x", "").Replace("0X", "");
            int v;
            if (int.TryParse(txt, System.Globalization.NumberStyles.HexNumber, null, out v))
            {
                v = Math.Max(0, Math.Min(65535, v));
                if (v != RegisterModel.RegisterValue)
                {
                    RegisterModel.RegisterValue = v;
                    RaiseEvent(new RoutedEventArgs(RegisterValueUpdatedEvent));
                }
            }
            else _hexBox.Text = $"0x{RegisterModel.RegisterValue:X4}";
            UpdateDisplays();
        }

        private void StrChanged()
        {
            if (_internal || RegisterModel == null) return;
            var s = _strBox.Text ?? "";
            int v = 0;
            if (s.Length >= 1) v |= (s[0] << 8);
            if (s.Length >= 2) v |= (s[1]);
            v &= 0xFFFF;
            if (v != RegisterModel.RegisterValue)
            {
                RegisterModel.RegisterValue = v;
                RaiseEvent(new RoutedEventArgs(RegisterValueUpdatedEvent));
            }
            UpdateDisplays();
        }

        private void UpdateDisplays()
        {
            if (RegisterModel == null) return;
            _internal = true;
            try
            {
                _header.Text = $"📍 Register {RegisterModel.DisplayAddress} (Addr: {RegisterModel.ModbusAddress}) ➤ {RegisterModel.RegisterValue}";
                if (!_decBox.IsFocused) _decBox.Text = RegisterModel.RegisterValue.ToString();
                if (!_hexBox.IsFocused) _hexBox.Text = $"0x{RegisterModel.RegisterValue:X4}";
                if (!_strBox.IsFocused)
                {
                    var c1 = (char)((RegisterModel.RegisterValue >> 8) & 0xFF);
                    var c2 = (char)(RegisterModel.RegisterValue & 0xFF);
                    var s = new StringBuilder();
                    if (c1 >= 32 && c1 <= 126) s.Append(c1);
                    if (c2 >= 32 && c2 <= 126) s.Append(c2);
                    _strBox.Text = s.ToString();
                }
                _binBox.Text = Convert.ToString(RegisterModel.RegisterValue & 0xFFFF, 2).PadLeft(16, '0');

                foreach (TextBox tb in _bitGrid.Children.OfType<TextBox>())
                {
                    if (!tb.IsFocused)
                    {
                        int bit = (int)tb.Tag;
                        int val = (RegisterModel.RegisterValue >> bit) & 1;
                        if (tb.Text != val.ToString())
                        {
                            tb.Text = val.ToString();
                            UpdateBitAppearance(tb, val);
                        }
                    }
                }
            }
            finally { _internal = false; }
        }

        private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctl = (ModernRegisterControl)d;
            ctl.UpdateDisplays();
        }
    }
    #endregion

    #region 장치/데이터스토어
    public class ModbusSlaveDevice
    {
        public byte UnitId { get; private set; }

        public ObservableCollection<RegisterModel> Coils;            // 01, 00001+
        public ObservableCollection<RegisterModel> DiscreteInputs;   // 02, 10001+
        public ObservableCollection<DualRegisterModel> HoldingRegisters; // 03, 40001+
        public ObservableCollection<DualRegisterModel> InputRegisters;   // 04, 30001+

        public ModbusSlaveDevice(byte unitId) { UnitId = unitId; }

        public void InitializeCoils(int start, int count)
        {
            var list = new ObservableCollection<RegisterModel>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new RegisterModel { DisplayAddress = 1 + start + i, ModbusAddress = start + i, Value = 0 });
            }
            Coils = list;
        }

        public void InitializeDiscreteInputs(int start, int count)
        {
            var list = new ObservableCollection<RegisterModel>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new RegisterModel { DisplayAddress = 10001 + start + i, ModbusAddress = start + i, Value = 0 });
            }
            DiscreteInputs = list;
        }

        public void InitializeHoldingRegisters(int start, int count)
        {
            var list = new ObservableCollection<DualRegisterModel>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new DualRegisterModel { DisplayAddress = 40001 + start + i, ModbusAddress = start + i, RegisterValue = 0 });
            }
            HoldingRegisters = list;
        }

        public void InitializeInputRegisters(int start, int count)
        {
            var list = new ObservableCollection<DualRegisterModel>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new DualRegisterModel { DisplayAddress = 30001 + start + i, ModbusAddress = start + i, RegisterValue = 0 });
            }
            InputRegisters = list;
        }
    }

    /// <summary>
    /// 4타입을 모두 담는 단일 DataStore (NModbus4 DataStore 상속)
    /// - UI/장치 캐시 <-> DataStore 동기화
    /// - 읽기/쓰기 이벤트 로깅
    /// </summary>
    public class UnifiedDataStore : DataStore
    {
        private readonly Dictionary<byte, DeviceCache> _cache = new Dictionary<byte, DeviceCache>();
        private Dictionary<byte, ModbusSlaveDevice> _devicesRef = new Dictionary<byte, ModbusSlaveDevice>();

        private volatile bool _updatingFromMaster = false;
        public bool IsUpdatingFromMaster => _updatingFromMaster;

        public UnifiedDataStore() : base()
        {
            // 넉넉한 초기 사이즈
            InitializeBlank(1000);
        }

        public void SetDevices(Dictionary<byte, ModbusSlaveDevice> devices) => _devicesRef = devices;

        public void AddDevice(byte unitId, ModbusSlaveDevice device)
        {
            if (!_cache.ContainsKey(unitId)) _cache[unitId] = new DeviceCache();
            UpdateDeviceCache(unitId, device);
        }

        public void RemoveDevice(byte unitId) => _cache.Remove(unitId);

        public void LoadFromDeviceCache(byte unitId, ModbusSlaveDevice device)
        {
            if (!_cache.ContainsKey(unitId))
            {
                _cache[unitId] = new DeviceCache();
                UpdateDeviceCache(unitId, device);
            }

            var c = _cache[unitId];
            ClearAndResize(1000);
            // Holding
            foreach (var kv in c.Holding) { int idx = kv.Key + 1; if (idx >= 0 && idx < HoldingRegisters.Count) HoldingRegisters[idx] = kv.Value; }
            // Input
            foreach (var kv in c.Input) { int idx = kv.Key + 1; if (idx >= 0 && idx < InputRegisters.Count) InputRegisters[idx] = kv.Value; }
            // Coil
            foreach (var kv in c.Coils) { int idx = kv.Key + 1; if (idx >= 0 && idx < CoilDiscretes.Count) CoilDiscretes[idx] = kv.Value; }
            // Discrete
            foreach (var kv in c.Discretes) { int idx = kv.Key + 1; if (idx >= 0 && idx < InputDiscretes.Count) InputDiscretes[idx] = kv.Value; }
        }

        public void UpdateDeviceCache(byte unitId, ModbusSlaveDevice device)
        {
            if (!_cache.ContainsKey(unitId)) _cache[unitId] = new DeviceCache();
            var c = _cache[unitId];

            if (device.HoldingRegisters != null)
                foreach (var r in device.HoldingRegisters)
                    c.Holding[r.ModbusAddress] = (ushort)Math.Max(0, Math.Min(65535, r.RegisterValue));

            if (device.InputRegisters != null)
                foreach (var r in device.InputRegisters)
                    c.Input[r.ModbusAddress] = (ushort)Math.Max(0, Math.Min(65535, r.RegisterValue));

            if (device.Coils != null)
                foreach (var r in device.Coils)
                    c.Coils[r.ModbusAddress] = r.Value != 0;

            if (device.DiscreteInputs != null)
                foreach (var r in device.DiscreteInputs)
                    c.Discretes[r.ModbusAddress] = r.Value != 0;
        }

        public void UpdateDeviceFromDataStoreToDevice(DataStoreEventArgs e, ModbusSlaveDevice device)
        {
            int idx = e.StartAddress + 1;

            if (e.ModbusDataType == ModbusDataType.HoldingRegister && device.HoldingRegisters != null)
            {
                var target = device.HoldingRegisters.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                if (target != null && idx < HoldingRegisters.Count)
                {
                    ushort val = HoldingRegisters[idx];
                    if (target.RegisterValue != val) target.RegisterValue = val;
                }
            }
            else if (e.ModbusDataType == ModbusDataType.Coil && device.Coils != null)
            {
                var target = device.Coils.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                if (target != null && idx < CoilDiscretes.Count)
                {
                    bool b = CoilDiscretes[idx];
                    int expect = b ? 1 : 0;
                    if (target.Value != expect) target.Value = expect;
                }
            }
        }

        public void RegisterEvents()
        {
            this.DataStoreWrittenTo += (s, e) =>
            {
                _updatingFromMaster = true;
                try { /* 호출 측에서 처리 */ }
                finally { _updatingFromMaster = false; }
            };
        }

        public void ClearAndResize(int size)
        {
            HoldingRegisters.Clear(); InputRegisters.Clear(); CoilDiscretes.Clear(); InputDiscretes.Clear();
            InitializeBlank(size);
        }

        private void InitializeBlank(int size)
        {
            for (int i = 0; i < size; i++)
            {
                HoldingRegisters.Add(0);
                InputRegisters.Add(0);
                CoilDiscretes.Add(false);
                InputDiscretes.Add(false);
            }
        }
    }

    public class DeviceCache
    {
        public Dictionary<int, ushort> Holding { get; set; } = new Dictionary<int, ushort>();
        public Dictionary<int, ushort> Input { get; set; } = new Dictionary<int, ushort>();
        public Dictionary<int, bool> Coils { get; set; } = new Dictionary<int, bool>();
        public Dictionary<int, bool> Discretes { get; set; } = new Dictionary<int, bool>();
    }
    #endregion
}
