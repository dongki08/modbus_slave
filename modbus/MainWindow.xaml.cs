using Modbus.Device;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Controls.Primitives;
using System.Linq;
using System.Text;
using Modbus.Data;
using System.Collections.Concurrent;
using System.Windows.Threading;
using System.Windows.Input;

namespace modbus
{
    public partial class MainWindow : Window
    {
        private TcpListener tcpListener;
        private ModbusTcpSlave modbusSlave;
        private CustomDataStore customDataStore;
        private Dictionary<byte, ModbusSlaveDevice> slaveDevices = new Dictionary<byte, ModbusSlaveDevice>();
        private bool isServerRunning = false;
        private CancellationTokenSource cancellationTokenSource;

        // UI 업데이트 최적화를 위한 타이머
        private DispatcherTimer uiUpdateTimer;
        private readonly object uiUpdateLock = new object();
        private volatile bool pendingUIUpdate = false;

        public MainWindow()
        {
            InitializeComponent();
            customDataStore = new CustomDataStore();

            StartAddressTextBox.ToolTip = "시작 주소 오프셋 (0부터 시작)\n" +
                                          "예: 0 입력 시 30001부터, 10 입력 시 30011부터";

            // UI 업데이트 타이머 초기화 (60fps)
            uiUpdateTimer = new DispatcherTimer();
            uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(16);
            uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            uiUpdateTimer.Start();
        }

        #region 커스텀 타이틀바 이벤트 핸들러

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                Maximize_Click(null, null);
            }
            else
            {
                this.DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
            }
            else
            {
                this.WindowState = WindowState.Maximized;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        #endregion

        private void OnRegisterValueUpdated(object sender, RoutedEventArgs e)
        {
            UpdateCurrentDeviceDataStore();
        }

        private void UiUpdateTimer_Tick(object sender, EventArgs e)
        {
            if (pendingUIUpdate)
            {
                lock (uiUpdateLock)
                {
                    if (pendingUIUpdate)
                    {
                        // 실제 UI 업데이트는 여기서 일괄 처리
                        pendingUIUpdate = false;
                    }
                }
            }
        }

        private async void StartServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (isServerRunning)
                {
                    ShowModernMessageBox("서버가 이미 실행 중입니다.", "정보", MessageBoxImage.Information);
                    return;
                }

                IPAddress ipAddress = IPAddress.Parse(IpTextBox.Text);
                int port = int.Parse(PortTextBox.Text);

                tcpListener = new TcpListener(ipAddress, port);
                tcpListener.Start();

                modbusSlave = ModbusTcpSlave.CreateTcp(0, tcpListener);
                modbusSlave.DataStore = customDataStore;

                modbusSlave.ModbusSlaveRequestReceived += (s, args) =>
                {
                    byte requestedUnitId = args.Message.SlaveAddress;
                    byte functionCode = args.Message.FunctionCode;

                    string functionName = GetFunctionCodeName(functionCode);
                    Log($"🔔 요청 수신 - 장치:{requestedUnitId}, 기능:{functionName}");

                    if (slaveDevices.ContainsKey(requestedUnitId))
                    {
                        customDataStore.LoadDeviceData(requestedUnitId, slaveDevices[requestedUnitId]);
                        Log($"✅ 장치 {requestedUnitId} 데이터 로드 완료");
                    }
                    else
                    {
                        Log($"❌ 경고: 요청된 장치 {requestedUnitId}가 존재하지 않습니다.");
                    }
                };

                customDataStore.RegisterDataStoreEvents();
                customDataStore.SetSlaveDevices(slaveDevices);

                isServerRunning = true;
                cancellationTokenSource = new CancellationTokenSource();

                _ = Task.Run(() => RunModbusServer(cancellationTokenSource.Token));

                // 서버 상태 업데이트 (성공 스타일)
                ServerStatusText.Text = "🟢 서버 실행중";
                var successBrush = FindResource("SuccessBrush") as SolidColorBrush;
                if (ServerStatusText.Parent is Border statusBorder)
                {
                    statusBorder.Background = new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromRgb(0x16, 0xA0, 0x85), 0),
                            new GradientStop(Color.FromRgb(0x13, 0x8D, 0x75), 1)
                        },
                        new Point(0, 0), new Point(0, 1)
                    );
                }

                Log($"🚀 서버 시작됨 - {ipAddress}:{port}");
                ShowModernMessageBox("서버가 성공적으로 시작되었습니다!", "성공", MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowModernMessageBox($"서버 시작 실패: {ex.Message}", "오류", MessageBoxImage.Error);
            }
        }

        private void ShowDeviceData_Click(object sender, RoutedEventArgs e)
        {
            DeviceDataButton.Style = (Style)FindResource("ActiveToggleButton");
            LogButton.Style = (Style)FindResource("ToggleButton2");

            HeaderIcon.Icon = FontAwesome.Sharp.IconChar.Database;
            HeaderText.Text = "장치 데이터";

            DeviceTabControl.Visibility = Visibility.Visible;
            LogContainer.Visibility = Visibility.Collapsed;

            DeleteDeviceButton.Visibility = Visibility.Visible;
        }

        private void ShowLog_Click(object sender, RoutedEventArgs e)
        {
            DeviceDataButton.Style = (Style)FindResource("ToggleButton2");
            LogButton.Style = (Style)FindResource("ActiveToggleButton");

            HeaderIcon.Icon = FontAwesome.Sharp.IconChar.FileLines;
            HeaderText.Text = "시스템 로그";

            DeviceTabControl.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Visible;

            DeleteDeviceButton.Visibility = Visibility.Collapsed;
        }

        private void StopServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                isServerRunning = false;
                cancellationTokenSource?.Cancel();

                modbusSlave?.Dispose();
                modbusSlave = null;

                tcpListener?.Stop();
                tcpListener = null;

                // 서버 상태 업데이트 (중지 스타일)
                ServerStatusText.Text = "🔴 서버 중지됨";
                var warningBrush = FindResource("WarningBrush") as SolidColorBrush;
                if (ServerStatusText.Parent is Border statusBorder)
                {
                    statusBorder.Background = new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromRgb(0xF3, 0x9C, 0x12), 0),
                            new GradientStop(Color.FromRgb(0xE6, 0x7E, 0x22), 1)
                        },
                        new Point(0, 0), new Point(0, 1)
                    );
                }

                Log("⏹ 서버 중지됨");
                ShowModernMessageBox("서버가 중지되었습니다.", "정보", MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowModernMessageBox($"서버 중지 실패: {ex.Message}", "오류", MessageBoxImage.Error);
            }
        }

        private async Task RunModbusServer(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    while (!cancellationToken.IsCancellationRequested && isServerRunning)
                    {
                        try
                        {
                            modbusSlave?.Listen();
                            Thread.Sleep(1);
                        }
                        catch (Exception ex)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                Dispatcher.BeginInvoke(new Action(() => Log($"❌ 서버 실행 오류: {ex.Message}")));
                            }

                            Thread.Sleep(100);
                        }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.BeginInvoke(new Action(() => Log("🔄 서버 종료됨")));
            }
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            byte unitId;
            int startAddress;
            int count;

            if (!byte.TryParse(UnitIdTextBox.Text, out unitId))
            {
                ShowModernMessageBox("장치 ID를 0-255 사이의 값으로 입력하세요.", "입력 오류", MessageBoxImage.Warning);
                return;
            }

            if (slaveDevices.ContainsKey(unitId))
            {
                ShowModernMessageBox("이미 존재하는 장치입니다.", "중복 오류", MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(StartAddressTextBox.Text, out startAddress) || startAddress < 0)
            {
                ShowModernMessageBox("올바른 시작 주소를 입력하세요. (0부터 시작)", "입력 오류", MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(AddressCountTextBox.Text, out count) || count <= 0)
            {
                ShowModernMessageBox("올바른 주소 수를 입력하세요.", "입력 오류", MessageBoxImage.Warning);
                return;
            }

            ComboBoxItem selectedItem = RegisterTypeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null)
            {
                ShowModernMessageBox("레지스터 유형을 선택하세요.", "선택 오류", MessageBoxImage.Warning);
                return;
            }

            string regType = selectedItem.Content.ToString().Substring(0, 2);
            ModbusSlaveDevice device = new ModbusSlaveDevice(unitId);

            switch (regType)
            {
                case "01": // Coils
                    device.InitializeCoils(startAddress, count);
                    break;
                case "02": // Discrete Inputs
                    device.InitializeDiscreteInputs(startAddress, count);
                    break;
                case "03": // Holding Registers
                    device.InitializeDualRegisters(startAddress, count, 40001);
                    break;
                case "04": // Input Registers
                    device.InitializeDualRegisters(startAddress, count, 30001);
                    break;
            }

            slaveDevices.Add(unitId, device);
            customDataStore.AddDevice(unitId, device);

            TabItem tab = new TabItem();
            tab.Header = $"장치 {unitId}";
            tab.Tag = unitId;
            tab.Content = CreateDeviceTab(device);
            DeviceTabControl.Items.Add(tab);
            DeviceTabControl.SelectedItem = tab;

            Log($"➕ 장치 {unitId} 추가됨 (유형: {selectedItem.Content}, 시작주소: {startAddress}, 개수: {count})");
        }

        private void DeleteDevice_Click(object sender, RoutedEventArgs e)
        {
            TabItem selectedTab = DeviceTabControl.SelectedItem as TabItem;
            if (selectedTab != null && selectedTab.Tag is byte)
            {
                byte unitId = (byte)selectedTab.Tag;

                var result = ShowModernMessageBox($"장치 {unitId}를 삭제하시겠습니까?", "삭제 확인", MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    DeviceTabControl.Items.Remove(selectedTab);
                    slaveDevices.Remove(unitId);
                    customDataStore.RemoveDevice(unitId);
                    Log($"🗑️ 장치 {unitId} 삭제됨");
                }
            }
            else
            {
                ShowModernMessageBox("삭제할 장치를 선택하세요.", "선택 오류", MessageBoxImage.Warning);
            }
        }

        private UIElement CreateDeviceTab(ModbusSlaveDevice device)
        {
            Grid mainGrid = new Grid();
            mainGrid.Margin = new Thickness(0);
            mainGrid.Background = Brushes.Transparent;

            int rowCount = 0;
            if (device.Coils != null) rowCount++;
            if (device.DiscreteInputs != null) rowCount++;
            if (device.DualRegisters != null) rowCount++;

            for (int i = 0; i < rowCount; i++)
            {
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }

            int currentRow = 0;

            if (device.Coils != null)
            {
                var card = CreateCoilCard("🔵 Coil [00001+]", device.Coils, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            if (device.DiscreteInputs != null)
            {
                var card = CreateCoilCard("🟢 Input Status [10001+]", device.DiscreteInputs, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            if (device.DualRegisters != null)
            {
                string title = device.RegisterType == 40001
                    ? "🟠 Holding Register [40001+]"
                    : "🟡 Input Register [30001+]";
                var card = CreateOptimizedRegisterCard(title, device.DualRegisters, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            return mainGrid;
        }

        // *** 최적화된 레지스터 카드 생성 - 모던 디자인 ***
        private UIElement CreateOptimizedRegisterCard(string title, ObservableCollection<DualRegisterModel> data,
    bool fillHeight = false)
{
    Grid cardContent = new Grid { Margin = new Thickness(0) }; // Border 제거하고 Grid로 직접 시작
    cardContent.Background = Brushes.Transparent;
    cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 헤더
    cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 콘텐츠

    // 헤더
    TextBlock header = new TextBlock
    {
        Text = title,
        FontSize = 16,
        FontWeight = FontWeights.Bold,
        Foreground = FindResource("TextBrush") as SolidColorBrush,
        Margin = new Thickness(0, 0, 0, 10),
        Effect = new DropShadowEffect
        {
            Color = Color.FromRgb(0x00, 0x78, 0xD4),
            BlurRadius = 8,
            ShadowDepth = 0,
            Opacity = 0.4
        }
    };
    Grid.SetRow(header, 0);
    cardContent.Children.Add(header);

    // 리스트박스
    ListBox virtualizedListBox = new ListBox();
    virtualizedListBox.ItemsSource = data;
    virtualizedListBox.Background = Brushes.Transparent;
    virtualizedListBox.BorderThickness = new Thickness(0);
    virtualizedListBox.Margin = new Thickness(0);
    virtualizedListBox.Padding = new Thickness(0);

    ScrollViewer.SetVerticalScrollBarVisibility(virtualizedListBox, ScrollBarVisibility.Auto);
    ScrollViewer.SetHorizontalScrollBarVisibility(virtualizedListBox, ScrollBarVisibility.Disabled);

    VirtualizingStackPanel.SetIsVirtualizing(virtualizedListBox, true);
    VirtualizingStackPanel.SetVirtualizationMode(virtualizedListBox, VirtualizationMode.Recycling);
    ScrollViewer.SetCanContentScroll(virtualizedListBox, true);

    virtualizedListBox.ItemTemplate = CreateOptimizedDataTemplate();

    // 항목 컨테이너 스타일 - 완전 투명
    virtualizedListBox.ItemContainerStyle = new Style(typeof(ListBoxItem));
    virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
    virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0)));
    virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(0)));
    virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent));
    virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.FocusableProperty, false));
    
    var noSelectionTemplate = new ControlTemplate(typeof(ListBoxItem));
    var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
    noSelectionTemplate.VisualTree = contentPresenter;
    virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.TemplateProperty, noSelectionTemplate));

    Grid.SetRow(virtualizedListBox, 1);
    cardContent.Children.Add(virtualizedListBox);

    return cardContent; // Border 대신 Grid 직접 반환
}

        // *** 최적화된 데이터 템플릿 생성 - 모던 스타일 ***
        private DataTemplate CreateOptimizedDataTemplate()
        {
            DataTemplate template = new DataTemplate();

            FrameworkElementFactory borderFactory = new FrameworkElementFactory(typeof(Border));

            // 그라데이션 배경 설정
            var gradientBrush = new LinearGradientBrush();
            gradientBrush.StartPoint = new Point(0, 0);
            gradientBrush.EndPoint = new Point(0, 1);
            gradientBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3F, 0x3F, 0x46), 0));
            gradientBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x32, 0x32, 0x37), 1));

            borderFactory.SetValue(Border.BackgroundProperty, gradientBrush);
            borderFactory.SetValue(Border.BorderBrushProperty, FindResource("BorderBrush"));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetValue(Border.MarginProperty, new Thickness(0, 0, 0,0));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(7));
            borderFactory.SetValue(Border.EffectProperty, FindResource("SoftShadow"));

            FrameworkElementFactory controlFactory = new FrameworkElementFactory(typeof(ModernRegisterControl));
            controlFactory.SetBinding(ModernRegisterControl.RegisterModelProperty, new Binding("."));
            controlFactory.AddHandler(ModernRegisterControl.RegisterValueUpdatedEvent,
                new RoutedEventHandler(OnRegisterValueUpdated));

            borderFactory.AppendChild(controlFactory);
            template.VisualTree = borderFactory;

            return template;
        }

        // Coil용 카드 생성 (모던 스타일)
        private UIElement CreateCoilCard(string title, ObservableCollection<RegisterModel> data,
            bool fillHeight = false)
        {
            Border cardBorder = new Border();
            cardBorder.Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x2D, 0x2D, 0x30), 0),
                    new GradientStop(Color.FromRgb(0x25, 0x25, 0x26), 1)
                },
                new Point(0, 0), new Point(0, 1)
            );
            cardBorder.BorderBrush = FindResource("BorderBrush") as SolidColorBrush;
            cardBorder.BorderThickness = new Thickness(1);
            cardBorder.CornerRadius = new CornerRadius(10);
            cardBorder.Margin = new Thickness(0);
            cardBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            cardBorder.VerticalAlignment = VerticalAlignment.Stretch;
            cardBorder.Effect = FindResource("CardShadow") as DropShadowEffect;

            Grid cardContent = new Grid();
            cardContent.Margin = new Thickness(0);
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            TextBlock header = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = FindResource("TextBrush") as SolidColorBrush,
                Margin = new Thickness(0, 0, 0, 5), // 15에서 5로 줄임
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x00, 0x78, 0xD4),
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.4
                }
            };
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            DataGrid grid = new DataGrid();
            grid.ItemsSource = data;
            grid.IsReadOnly = false;
            grid.AutoGenerateColumns = false;
            grid.CanUserAddRows = false;
            grid.CanUserDeleteRows = false;
            grid.CanUserResizeColumns = false;
            grid.CanUserResizeRows = false;
            grid.Background = Brushes.Transparent;
            grid.BorderThickness = new Thickness(0);
            grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            grid.HorizontalGridLinesBrush = FindResource("BorderBrush") as SolidColorBrush;
            grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            grid.RowHeight = 32;
            grid.FontSize = 13;
            grid.Foreground = FindResource("TextBrush") as SolidColorBrush;
            grid.HorizontalAlignment = HorizontalAlignment.Stretch;
            grid.VerticalAlignment = VerticalAlignment.Stretch;
            grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            // *** 가상화 활성화 ***
            grid.EnableRowVirtualization = true;
            grid.EnableColumnVirtualization = true;
            VirtualizingStackPanel.SetIsVirtualizing(grid, true);
            VirtualizingStackPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);

            Grid.SetRow(grid, 1);

            grid.ColumnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty,
                FindResource("SurfaceBrush")));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty,
                FindResource("TextBrush")));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty,
                FontWeights.Bold));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 13.0));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.HeightProperty, 35.0));

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Address",
                Binding = new Binding("DisplayAddress"),
                IsReadOnly = true,
                Width = new DataGridLength(0.4, DataGridLengthUnitType.Star),
                MinWidth = 100
            });

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Binding = new Binding("Value"),
                IsReadOnly = false,
                Width = new DataGridLength(0.6, DataGridLengthUnitType.Star),
                MinWidth = 80
            });

            grid.CellEditEnding += (sender, e) =>
            {
                if (e.EditAction == DataGridEditAction.Commit)
                {
                    var register = e.Row.Item as RegisterModel;
                    if (register != null)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Log($"📝 레지스터 {register.DisplayAddress} 값이 {register.Value}로 변경됨");
                            UpdateCurrentDeviceDataStore();
                        }), DispatcherPriority.Background);
                    }
                }
            };

            cardContent.Children.Add(grid);
            cardBorder.Child = cardContent;
            return cardBorder;
        }
        
        private void UpdateCurrentDeviceDataStore()
        {
            if (customDataStore.IsUpdatingFromMaster)
            {
                System.Diagnostics.Debug.WriteLine("마스터 업데이트 중 - UI 변경 무시");
                return;
            }

            TabItem selectedTab = DeviceTabControl.SelectedItem as TabItem;
            if (selectedTab != null && selectedTab.Tag is byte)
            {
                byte unitId = (byte)selectedTab.Tag;
                if (slaveDevices.ContainsKey(unitId))
                {
                    var device = slaveDevices[unitId];
                    customDataStore.UpdateDeviceCache(unitId, device);
                    System.Diagnostics.Debug.WriteLine($"UI 변경 감지 - 장치 {unitId} 캐시 업데이트 완료");
                }
            }
        }

        private string GetFunctionCodeName(byte functionCode)
        {
            switch (functionCode)
            {
                case 1: return "Read Coils";
                case 2: return "Read Discrete Inputs";
                case 3: return "Read Holding Registers";
                case 4: return "Read Input Registers";
                case 5: return "Write Single Coil";
                case 6: return "Write Single Register";
                case 15: return "Write Multiple Coils";
                case 16: return "Write Multiple Registers";
                default: return $"Unknown Function Code {functionCode}";
            }
        }

        private void Log(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.Items.Add($"⏰ {DateTime.Now:HH:mm:ss} - {message}");
                if (LogBox.Items.Count > 100)
                    LogBox.Items.RemoveAt(0);
                LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
            }), DispatcherPriority.Background);
        }

        private MessageBoxResult ShowModernMessageBox(string message, string title, MessageBoxImage icon)
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, icon);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            uiUpdateTimer?.Stop();
            if (isServerRunning)
            {
                StopServer_Click(null, null);
            }

            base.OnClosing(e);
        }
    }

    // *** 모던 레지스터 컨트롤 (UserControl 기반) ***
    public class ModernRegisterControl : UserControl
    {
        public static readonly DependencyProperty RegisterModelProperty =
            DependencyProperty.Register("RegisterModel", typeof(DualRegisterModel), typeof(ModernRegisterControl),
                new PropertyMetadata(null, OnRegisterModelChanged));

        public DualRegisterModel RegisterModel
        {
            get { return (DualRegisterModel)GetValue(RegisterModelProperty); }
            set { SetValue(RegisterModelProperty, value); }
        }

        private TextBox decimalTextBox;
        private TextBox hexTextBox;
        private TextBox stringTextBox;
        private TextBox binaryTextBox;
        private Grid bitGrid;
        private TextBlock headerLabel;
        private bool isInternalUpdate = false;

        private static readonly Dictionary<int, ModernRegisterControl> activeControls =
            new Dictionary<int, ModernRegisterControl>();

        public ModernRegisterControl()
        {
            CreateUI();
        }

        private void CreateUI()
        {
            Grid mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 헤더 (모던 스타일)
            headerLabel = new TextBlock();
            headerLabel.FontWeight = FontWeights.Bold;
            headerLabel.FontSize = 14;
            headerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            headerLabel.Margin = new Thickness(0, 0, 0, 12);
            headerLabel.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x00, 0x78, 0xD4),
                BlurRadius = 5,
                ShadowDepth = 0,
                Opacity = 0.6
            };
            Grid.SetRow(headerLabel, 0);
            mainGrid.Children.Add(headerLabel);

            // 입력 컨트롤 행
            StackPanel inputRow = new StackPanel();
            inputRow.Orientation = Orientation.Horizontal;
            inputRow.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(inputRow, 1);

            // 10진수 입력
            CreateDecimalInput(inputRow);

            // 16진수 입력
            CreateHexInput(inputRow);

            // 문자열 입력
            CreateStringInput(inputRow);

            // 2진수 표시
            CreateBinaryDisplay(inputRow);

            // 비트 편집
            CreateBitEditor(inputRow);

            mainGrid.Children.Add(inputRow);
            this.Content = mainGrid;
        }

        private void CreateDecimalInput(StackPanel parent)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.Margin = new Thickness(0, 0, 15, 0);

            TextBlock label = new TextBlock();
            label.Text = "10진수";
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 8, 0);
            label.FontSize = 12;
            label.FontWeight = FontWeights.Medium;

            decimalTextBox = CreateModernTextBox(60);

            decimalTextBox.LostFocus += (s, e) => ProcessDecimalInput();
            decimalTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    ProcessDecimalInput();
                    e.Handled = true;
                }
            };

            panel.Children.Add(label);
            panel.Children.Add(decimalTextBox);
            parent.Children.Add(panel);
        }

        private void CreateHexInput(StackPanel parent)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.Margin = new Thickness(0, 0, 15, 0);

            TextBlock label = new TextBlock();
            label.Text = "16진수";
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 8, 0);
            label.FontSize = 12;
            label.FontWeight = FontWeights.Medium;

            hexTextBox = CreateModernTextBox(70);

            hexTextBox.LostFocus += (s, e) => ProcessHexInput();
            hexTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    ProcessHexInput();
                    e.Handled = true;
                }
            };

            panel.Children.Add(label);
            panel.Children.Add(hexTextBox);
            parent.Children.Add(panel);
        }

        private void CreateStringInput(StackPanel parent)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.Margin = new Thickness(0, 0, 15, 0);

            TextBlock label = new TextBlock();
            label.Text = "문자열";
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 8, 0);
            label.FontSize = 12;
            label.FontWeight = FontWeights.Medium;

            stringTextBox = CreateModernTextBox(45);
            stringTextBox.MaxLength = 2;

            stringTextBox.LostFocus += (s, e) => ProcessStringInput();
            stringTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    ProcessStringInput();
                    e.Handled = true;
                }
            };

            panel.Children.Add(label);
            panel.Children.Add(stringTextBox);
            parent.Children.Add(panel);
        }

        private void CreateBinaryDisplay(StackPanel parent)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.Margin = new Thickness(0, 0, 15, 0);

            TextBlock label = new TextBlock();
            label.Text = "2진수";
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 8, 0);
            label.FontSize = 12;
            label.FontWeight = FontWeights.Medium;

            binaryTextBox = CreateModernTextBox(140);
            binaryTextBox.IsReadOnly = true;
            binaryTextBox.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            binaryTextBox.FontFamily = new FontFamily("Consolas, Monaco, monospace");
            binaryTextBox.FontSize = 11;
            binaryTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));

            panel.Children.Add(label);
            panel.Children.Add(binaryTextBox);
            parent.Children.Add(panel);
        }

        private TextBox CreateModernTextBox(double width)
        {
            TextBox textBox = new TextBox();
            textBox.Width = width;
            textBox.Height = 28;
            textBox.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
            textBox.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            textBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            textBox.BorderThickness = new Thickness(1);
            textBox.Padding = new Thickness(8, 4, 0,0);
            textBox.FontSize = 12;
            textBox.VerticalContentAlignment = VerticalAlignment.Center;

            // 모던 스타일 효과
            var style = new Style(typeof(TextBox));
            var template = new ControlTemplate(typeof(TextBox));

            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "border";
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty,
                new TemplateBindingExtension(TextBox.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty,
                new TemplateBindingExtension(TextBox.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

            var scrollViewerFactory = new FrameworkElementFactory(typeof(ScrollViewer));
            scrollViewerFactory.Name = "PART_ContentHost";
            scrollViewerFactory.SetValue(ScrollViewer.FocusableProperty, false);
            scrollViewerFactory.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                ScrollBarVisibility.Hidden);
            scrollViewerFactory.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            scrollViewerFactory.SetValue(ScrollViewer.MarginProperty,
                new TemplateBindingExtension(TextBox.PaddingProperty));

            borderFactory.AppendChild(scrollViewerFactory);
            template.VisualTree = borderFactory;

            // 포커스 트리거
            var focusTrigger = new Trigger();
            focusTrigger.Property = TextBox.IsFocusedProperty;
            focusTrigger.Value = true;
            focusTrigger.Setters.Add(new Setter(TextBox.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), "border"));
            focusTrigger.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(2), "border"));
            focusTrigger.Setters.Add(new Setter(TextBox.EffectProperty, new DropShadowEffect
            {
                Color = Color.FromRgb(0x00, 0x78, 0xD4),
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.6
            }, "border"));

            template.Triggers.Add(focusTrigger);
            textBox.Template = template;

            return textBox;
        }

        private void CreateBitEditor(StackPanel parent)
        {
            StackPanel bitSection = new StackPanel();
            bitSection.Orientation = Orientation.Horizontal;

            TextBlock label = new TextBlock();
            label.Text = "비트";
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 8, 0);
            label.FontSize = 12;
            label.FontWeight = FontWeights.Medium;

            bitGrid = new Grid();
            bitGrid.HorizontalAlignment = HorizontalAlignment.Left;

            // 행 정의
            bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 비트 번호
            bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 비트 값

            // 16개 열 정의
            for (int i = 0; i < 16; i++)
            {
                bitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            }

            // 비트 번호 라벨 생성 (15부터 0까지)
            for (int bit = 15; bit >= 0; bit--)
            {
                TextBlock bitNumberLabel = new TextBlock();
                bitNumberLabel.Text = bit.ToString();
                bitNumberLabel.FontSize = 8;
                bitNumberLabel.FontWeight = FontWeights.Bold;
                bitNumberLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
                bitNumberLabel.TextAlignment = TextAlignment.Center;
                bitNumberLabel.HorizontalAlignment = HorizontalAlignment.Center;
                bitNumberLabel.Margin = new Thickness(1, 0, 1, 2);

                Grid.SetRow(bitNumberLabel, 0);
                Grid.SetColumn(bitNumberLabel, 15 - bit);
                bitGrid.Children.Add(bitNumberLabel);
            }

            // 비트 텍스트박스 생성 (15부터 0까지)
            for (int bit = 15; bit >= 0; bit--)
            {
                TextBox bitTextBox = CreateModernBitTextBox();
                bitTextBox.Tag = bit;

                bitTextBox.TextChanged += BitTextBox_TextChanged;
                bitTextBox.KeyDown += BitTextBox_KeyDown;
                bitTextBox.GotFocus += (s, e) => ((TextBox)s).SelectAll();

                bitTextBox.MouseDoubleClick += (sender, e) =>
                {
                    var tb = sender as TextBox;
                    string newValue = (tb.Text == "0") ? "1" : "0";
                    tb.Text = newValue;
                };

                Grid.SetRow(bitTextBox, 1);
                Grid.SetColumn(bitTextBox, 15 - bit);
                bitGrid.Children.Add(bitTextBox);
            }

            bitSection.Children.Add(label);
            bitSection.Children.Add(bitGrid);
            parent.Children.Add(bitSection);
        }

        private TextBox CreateModernBitTextBox()
        {
            TextBox bitTextBox = new TextBox();
            bitTextBox.Width = 20;
            bitTextBox.Height = 28;
            bitTextBox.FontSize = 10;
            bitTextBox.FontWeight = FontWeights.Bold;
            bitTextBox.TextAlignment = TextAlignment.Center;
            bitTextBox.VerticalContentAlignment = VerticalAlignment.Center;
            bitTextBox.MaxLength = 1;
            bitTextBox.IsTabStop = false;
            bitTextBox.Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
            bitTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            bitTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            bitTextBox.BorderThickness = new Thickness(1);

            // 둥근 모서리
            var template = new ControlTemplate(typeof(TextBox));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty,
                new TemplateBindingExtension(TextBox.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty,
                new TemplateBindingExtension(TextBox.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));

            var contentPresenter = new FrameworkElementFactory(typeof(ScrollViewer));
            contentPresenter.Name = "PART_ContentHost";
            contentPresenter.SetValue(ScrollViewer.FocusableProperty, false);
            contentPresenter.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            contentPresenter.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);

            borderFactory.AppendChild(contentPresenter);
            template.VisualTree = borderFactory;
            bitTextBox.Template = template;

            return bitTextBox;
        }

        private void BitTextBox_TextChanged(object sender, RoutedEventArgs e)
        {
            if (isInternalUpdate) return;

            var tb = sender as TextBox;
            if (tb.Text == "0" || tb.Text == "1")
            {
                int newBitValue = int.Parse(tb.Text);
                UpdateBitAppearance(tb, newBitValue);
                UpdateRegisterFromBits();
            }
            else if (!string.IsNullOrEmpty(tb.Text))
            {
                int bitPos = (int)tb.Tag;
                int originalValue = (RegisterModel.RegisterValue >> bitPos) & 1;
                tb.Text = originalValue.ToString();
                UpdateBitAppearance(tb, originalValue);
            }
        }

        private void BitTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var tb = sender as TextBox;

            if (e.Key == System.Windows.Input.Key.D0 || e.Key == System.Windows.Input.Key.NumPad0)
            {
                tb.Text = "0";
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.D1 || e.Key == System.Windows.Input.Key.NumPad1)
            {
                tb.Text = "1";
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Left || e.Key == System.Windows.Input.Key.Right)
            {
                MoveBitFocus(tb, e.Key == System.Windows.Input.Key.Left);
                e.Handled = true;
            }
            else
            {
                e.Handled = true;
            }
        }

        private void MoveBitFocus(TextBox currentBit, bool moveLeft)
        {
            int currentPos = (int)currentBit.Tag;
            int targetPos = moveLeft ? currentPos - 1 : currentPos + 1;

            if (targetPos >= 0 && targetPos <= 15)
            {
                var targetBit = bitGrid.Children.OfType<TextBox>()
                    .FirstOrDefault(t => (int)t.Tag == targetPos);
                if (targetBit != null)
                {
                    targetBit.Focus();
                    targetBit.SelectAll();
                }
            }
        }

        private void UpdateBitAppearance(TextBox textBox, int bitValue)
        {
            if (bitValue == 1)
            {
                // 네온 그린 효과
                textBox.Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x39, 0xFF, 0x14), 0),
                        new GradientStop(Color.FromRgb(0x00, 0xFF, 0x41), 1)
                    },
                    new Point(0, 0), new Point(0, 1)
                );
                textBox.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
                textBox.Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x00, 0xFF, 0x41),
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.8
                };
            }
            else
            {
                textBox.Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
                textBox.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
                textBox.Effect = null;
            }
        }

        private void ProcessDecimalInput()
        {
            if (isInternalUpdate || RegisterModel == null) return;

            if (int.TryParse(decimalTextBox.Text, out int value))
            {
                value = Math.Max(0, Math.Min(65535, value));
                if (value != RegisterModel.RegisterValue)
                {
                    RegisterModel.RegisterValue = value;
                    UpdateCurrentDeviceDataStore();
                }
            }
            else
            {
                decimalTextBox.Text = RegisterModel.RegisterValue.ToString();
            }
        }

        private void ProcessHexInput()
        {
            if (isInternalUpdate || RegisterModel == null) return;

            string input = hexTextBox.Text.Trim().Replace("0x", "").Replace("0X", "");
            if (int.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out int value))
            {
                value = Math.Max(0, Math.Min(65535, value));
                if (value != RegisterModel.RegisterValue)
                {
                    RegisterModel.RegisterValue = value;
                    UpdateCurrentDeviceDataStore();
                }
            }
            else
            {
                hexTextBox.Text = $"0x{RegisterModel.RegisterValue:X4}";
            }
        }

        private void ProcessStringInput()
        {
            if (isInternalUpdate || RegisterModel == null) return;

            int value = ConvertStringToRegisterValue(stringTextBox.Text);
            if (value != RegisterModel.RegisterValue)
            {
                RegisterModel.RegisterValue = value;
                UpdateCurrentDeviceDataStore();
            }
        }

        private void UpdateRegisterFromBits()
        {
            if (isInternalUpdate || RegisterModel == null) return;

            int newValue = 0;
            foreach (TextBox tb in bitGrid.Children.OfType<TextBox>())
            {
                if (int.TryParse(tb.Text, out int bitValue) && (bitValue == 0 || bitValue == 1))
                {
                    int bitPos = (int)tb.Tag;
                    if (bitValue == 1)
                    {
                        newValue |= (1 << bitPos);
                    }
                }
            }

            if (newValue != RegisterModel.RegisterValue)
            {
                RegisterModel.RegisterValue = newValue;
                UpdateCurrentDeviceDataStore();
            }
        }

        private void UpdateAllDisplays()
        {
            if (RegisterModel == null) return;

            isInternalUpdate = true;

            try
            {
                // 헤더 업데이트 (네온 스타일)
                headerLabel.Text =
                    $"📍 Register {RegisterModel.DisplayAddress} (Address: {RegisterModel.ModbusAddress}) ➤ Value: {RegisterModel.RegisterValue}";

                // 텍스트박스 업데이트 (포커스된 것은 제외)
                if (!decimalTextBox.IsFocused)
                    decimalTextBox.Text = RegisterModel.RegisterValue.ToString();

                if (!hexTextBox.IsFocused)
                    hexTextBox.Text = $"0x{RegisterModel.RegisterValue:X4}";

                if (!stringTextBox.IsFocused)
                    stringTextBox.Text = ExtractStringFromRegister(RegisterModel.RegisterValue);

                binaryTextBox.Text = Convert.ToString(RegisterModel.RegisterValue & 0xFFFF, 2).PadLeft(16, '0');

                // 비트 업데이트
                foreach (TextBox tb in bitGrid.Children.OfType<TextBox>())
                {
                    if (!tb.IsFocused)
                    {
                        int bitPos = (int)tb.Tag;
                        int bitValue = (RegisterModel.RegisterValue >> bitPos) & 1;
                        if (tb.Text != bitValue.ToString())
                        {
                            tb.Text = bitValue.ToString();
                            UpdateBitAppearance(tb, bitValue);
                        }
                    }
                }
            }
            finally
            {
                isInternalUpdate = false;
            }
        }

        private string ExtractStringFromRegister(int registerValue)
        {
            StringBuilder sb = new StringBuilder();

            char char1 = (char)((registerValue >> 8) & 0xFF);
            if (char1 >= 32 && char1 <= 126)
                sb.Append(char1);

            char char2 = (char)(registerValue & 0xFF);
            if (char2 >= 32 && char2 <= 126)
                sb.Append(char2);

            return sb.ToString();
        }

        private int ConvertStringToRegisterValue(string input)
        {
            if (string.IsNullOrEmpty(input))
                return 0;

            int value = 0;
            if (input.Length >= 1)
                value |= ((int)input[0] << 8);
            if (input.Length >= 2)
                value |= (int)input[1];

            return value & 0xFFFF;
        }

        public static readonly RoutedEvent RegisterValueUpdatedEvent = EventManager.RegisterRoutedEvent(
            "RegisterValueUpdated", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ModernRegisterControl));

        public event RoutedEventHandler RegisterValueUpdated
        {
            add { AddHandler(RegisterValueUpdatedEvent, value); }
            remove { RemoveHandler(RegisterValueUpdatedEvent, value); }
        }

        private void UpdateCurrentDeviceDataStore()
        {
            RaiseEvent(new RoutedEventArgs(RegisterValueUpdatedEvent));
        }

        private static void OnRegisterModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ModernRegisterControl;
            if (control == null) return;

            if (e.OldValue is DualRegisterModel oldModel)
            {
                oldModel.PropertyChanged -= control.OnRegisterPropertyChanged;
                activeControls.Remove(oldModel.GetHashCode());
            }

            if (e.NewValue is DualRegisterModel newModel)
            {
                newModel.PropertyChanged += control.OnRegisterPropertyChanged;
                activeControls[newModel.GetHashCode()] = control;
                control.UpdateAllDisplays();
            }
        }

        private void OnRegisterPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DualRegisterModel.RegisterValue))
            {
                Dispatcher.BeginInvoke(new Action(UpdateAllDisplays), DispatcherPriority.Render);
            }
        }
    }

    // 기존 RegisterModel (최적화)
    public class RegisterModel : INotifyPropertyChanged
    {
        private int _value;
        public int DisplayAddress { get; set; }
        public int ModbusAddress { get; set; }

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

    // 최적화된 DualRegisterModel
    public class DualRegisterModel : INotifyPropertyChanged
    {
        private int _registerValue;
        public int DisplayAddress { get; set; }
        public int ModbusAddress { get; set; }

        public string HeaderText => $"Register {DisplayAddress} (Address: {ModbusAddress}) - Value: {RegisterValue}";

        public int RegisterValue
        {
            get => _registerValue;
            set
            {
                if (_registerValue != value)
                {
                    _registerValue = Math.Max(0, Math.Min(65535, value));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RegisterValue)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderText)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    // ModbusSlaveDevice 클래스 (최적화)
    public class ModbusSlaveDevice
    {
        public byte UnitId { get; private set; }
        public int RegisterType { get; private set; }

        public ObservableCollection<RegisterModel> Coils;
        public ObservableCollection<RegisterModel> DiscreteInputs;
        public ObservableCollection<DualRegisterModel> DualRegisters;

        public ModbusSlaveDevice(byte unitId)
        {
            UnitId = unitId;
        }

        public void InitializeCoils(int startAddr, int count)
        {
            Coils = CreateCoilRegisters(startAddr, count, 1);
        }

        public void InitializeDiscreteInputs(int startAddr, int count)
        {
            DiscreteInputs = CreateCoilRegisters(startAddr, count, 10001);
        }

        public void InitializeDualRegisters(int startAddr, int count, int baseAddr)
        {
            RegisterType = baseAddr;
            DualRegisters = CreateDualRegisters(startAddr, count, baseAddr);
        }

        private ObservableCollection<RegisterModel> CreateCoilRegisters(int startAddr, int count, int baseAddr)
        {
            var list = new ObservableCollection<RegisterModel>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new RegisterModel
                {
                    DisplayAddress = baseAddr + startAddr + i,
                    ModbusAddress = startAddr + i,
                    Value = 0
                });
            }

            return list;
        }

        private ObservableCollection<DualRegisterModel> CreateDualRegisters(int startAddr, int count, int baseAddr)
        {
            var list = new ObservableCollection<DualRegisterModel>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new DualRegisterModel
                {
                    DisplayAddress = baseAddr + startAddr + i,
                    ModbusAddress = startAddr + i,
                    RegisterValue = 0
                });
            }

            return list;
        }
    }

    // 최적화된 CustomDataStore
    public class CustomDataStore : DataStore
    {
        private Dictionary<byte, ModbusSlaveDevice> devices = new Dictionary<byte, ModbusSlaveDevice>();
        private Dictionary<byte, DeviceDataCache> deviceDataCache = new Dictionary<byte, DeviceDataCache>();
        private volatile bool isUpdatingFromMaster = false;

        public bool IsUpdatingFromMaster => isUpdatingFromMaster;

        public CustomDataStore() : base()
        {
            InitializeDataStoreWithDefaultSize();
        }

        private void InitializeDataStoreWithDefaultSize()
        {
            try
            {
                for (int i = 0; i < 1000; i++)
                {
                    HoldingRegisters.Add(0);
                    InputRegisters.Add(0);
                    CoilDiscretes.Add(false);
                    InputDiscretes.Add(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DataStore 초기화 오류: {ex.Message}");
            }
        }

        public void SetSlaveDevices(Dictionary<byte, ModbusSlaveDevice> slaveDevices)
        {
            devices = slaveDevices;
        }

        public void AddDevice(byte unitId, ModbusSlaveDevice device)
        {
            devices[unitId] = device;
            if (!deviceDataCache.ContainsKey(unitId))
            {
                deviceDataCache[unitId] = new DeviceDataCache();
            }

            UpdateDeviceCache(unitId, device);
        }

        public void RemoveDevice(byte unitId)
        {
            devices.Remove(unitId);
            deviceDataCache.Remove(unitId);
        }

        public void LoadDeviceData(byte unitId, ModbusSlaveDevice device)
        {
            if (!deviceDataCache.ContainsKey(unitId))
            {
                deviceDataCache[unitId] = new DeviceDataCache();
                UpdateDeviceCache(unitId, device);
            }

            var cache = deviceDataCache[unitId];
            ClearDataStore();

            // 캐시에서 DataStore로 로드 (최적화된 방식)
            LoadFromCache(cache);
        }

        private void LoadFromCache(DeviceDataCache cache)
        {
            foreach (var kvp in cache.HoldingRegisters)
            {
                int index = kvp.Key + 1;
                if (index >= 0 && index < HoldingRegisters.Count)
                {
                    HoldingRegisters[index] = kvp.Value;
                }
            }

            foreach (var kvp in cache.InputRegisters)
            {
                int index = kvp.Key + 1;
                if (index >= 0 && index < InputRegisters.Count)
                {
                    InputRegisters[index] = kvp.Value;
                }
            }

            foreach (var kvp in cache.CoilDiscretes)
            {
                int index = kvp.Key + 1;
                if (index >= 0 && index < CoilDiscretes.Count)
                {
                    CoilDiscretes[index] = kvp.Value;
                }
            }

            foreach (var kvp in cache.InputDiscretes)
            {
                int index = kvp.Key + 1;
                if (index >= 0 && index < InputDiscretes.Count)
                {
                    InputDiscretes[index] = kvp.Value;
                }
            }
        }

        private void ClearDataStore()
        {
            try
            {
                HoldingRegisters.Clear();
                InputRegisters.Clear();
                CoilDiscretes.Clear();
                InputDiscretes.Clear();

                for (int i = 0; i < 1000; i++)
                {
                    HoldingRegisters.Add(0);
                    InputRegisters.Add(0);
                    CoilDiscretes.Add(false);
                    InputDiscretes.Add(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DataStore 초기화 오류: {ex.Message}");
            }
        }

        public void UpdateDeviceCache(byte unitId, ModbusSlaveDevice device)
        {
            if (!deviceDataCache.ContainsKey(unitId))
                deviceDataCache[unitId] = new DeviceDataCache();

            var cache = deviceDataCache[unitId];

            if (device.DualRegisters != null)
            {
                foreach (var reg in device.DualRegisters)
                {
                    ushort value = (ushort)Math.Max(0, Math.Min(65535, reg.RegisterValue));

                    if (device.RegisterType == 40001)
                    {
                        cache.HoldingRegisters[reg.ModbusAddress] = value;
                    }
                    else if (device.RegisterType == 30001)
                    {
                        cache.InputRegisters[reg.ModbusAddress] = value;
                    }
                }
            }

            if (device.Coils != null)
            {
                foreach (var reg in device.Coils)
                {
                    cache.CoilDiscretes[reg.ModbusAddress] = reg.Value != 0;
                }
            }

            if (device.DiscreteInputs != null)
            {
                foreach (var reg in device.DiscreteInputs)
                {
                    cache.InputDiscretes[reg.ModbusAddress] = reg.Value != 0;
                }
            }
        }

        public void RegisterDataStoreEvents()
        {
            this.DataStoreWrittenTo += OnDataStoreWrittenTo;
        }

        private void OnDataStoreWrittenTo(object sender, DataStoreEventArgs e)
        {
            isUpdatingFromMaster = true;

            try
            {
                UpdateAllDeviceCachesFromDataStore(e);
                UpdateCurrentDeviceUI(e);
            }
            finally
            {
                isUpdatingFromMaster = false;
            }
        }

        private void UpdateAllDeviceCachesFromDataStore(DataStoreEventArgs e)
        {
            foreach (var deviceKvp in devices)
            {
                byte unitId = deviceKvp.Key;
                var device = deviceKvp.Value;

                if (!deviceDataCache.ContainsKey(unitId)) continue;

                var cache = deviceDataCache[unitId];
                UpdateCacheFromDataStore(e, device, cache);
            }
        }

        private void UpdateCacheFromDataStore(DataStoreEventArgs e, ModbusSlaveDevice device, DeviceDataCache cache)
        {
            int dataStoreIndex = e.StartAddress + 1;

            if (e.ModbusDataType == ModbusDataType.HoldingRegister &&
                device.DualRegisters != null && device.RegisterType == 40001)
            {
                if (device.DualRegisters.Any(r => r.ModbusAddress == e.StartAddress) &&
                    dataStoreIndex < HoldingRegisters.Count)
                {
                    cache.HoldingRegisters[e.StartAddress] = HoldingRegisters[dataStoreIndex];
                }
            }
            else if (e.ModbusDataType == ModbusDataType.Coil && device.Coils != null)
            {
                if (device.Coils.Any(r => r.ModbusAddress == e.StartAddress) &&
                    dataStoreIndex < CoilDiscretes.Count)
                {
                    cache.CoilDiscretes[e.StartAddress] = CoilDiscretes[dataStoreIndex];
                }
            }
        }

        private void UpdateCurrentDeviceUI(DataStoreEventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow?.DeviceTabControl?.SelectedItem is TabItem selectedTab &&
                        selectedTab.Tag is byte currentUnitId && devices.ContainsKey(currentUnitId))
                    {
                        var device = devices[currentUnitId];
                        UpdateDeviceFromDataStore(e, device);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UI 업데이트 오류: {ex.Message}");
                }
            }), DispatcherPriority.Background);
        }

        private void UpdateDeviceFromDataStore(DataStoreEventArgs e, ModbusSlaveDevice device)
        {
            int dataStoreIndex = e.StartAddress + 1;

            if (e.ModbusDataType == ModbusDataType.HoldingRegister &&
                device.DualRegisters != null && device.RegisterType == 40001)
            {
                var targetRegister = device.DualRegisters.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                if (targetRegister != null && dataStoreIndex < HoldingRegisters.Count)
                {
                    ushort newValue = HoldingRegisters[dataStoreIndex];
                    if (targetRegister.RegisterValue != newValue)
                    {
                        targetRegister.RegisterValue = newValue;
                    }
                }
            }
            else if (e.ModbusDataType == ModbusDataType.Coil && device.Coils != null)
            {
                var targetRegister = device.Coils.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                if (targetRegister != null && dataStoreIndex < CoilDiscretes.Count)
                {
                    bool newValue = CoilDiscretes[dataStoreIndex];
                    int expectedValue = newValue ? 1 : 0;
                    if (targetRegister.Value != expectedValue)
                    {
                        targetRegister.Value = expectedValue;
                    }
                }
            }
        }
    }

    // 장치별 데이터 캐시 클래스
    public class DeviceDataCache
    {
        public Dictionary<int, ushort> HoldingRegisters { get; set; } = new Dictionary<int, ushort>();
        public Dictionary<int, ushort> InputRegisters { get; set; } = new Dictionary<int, ushort>();
        public Dictionary<int, bool> CoilDiscretes { get; set; } = new Dictionary<int, bool>();
        public Dictionary<int, bool> InputDiscretes { get; set; } = new Dictionary<int, bool>();
    }
}