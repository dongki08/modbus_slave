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
                    MessageBox.Show("서버가 이미 실행 중입니다.");
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

                ServerStatusText.Text = "서버 실행중";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.Green;

                Log($"서버 시작됨 - {ipAddress}:{port}");
                MessageBox.Show("서버가 시작되었습니다.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"서버 시작 실패: {ex.Message}");
            }
        }
        
        private void ShowDeviceData_Click(object sender, RoutedEventArgs e)
        {
            DeviceDataButton.Style = (Style)FindResource("ActiveToggleButton");
            LogButton.Style = (Style)FindResource("ToggleButton");
    
            HeaderIcon.Icon = FontAwesome.Sharp.IconChar.Database;
            HeaderText.Text = "장치 데이터";
    
            DeviceTabControl.Visibility = Visibility.Visible;
            LogContainer.Visibility = Visibility.Collapsed;
    
            DeleteDeviceButton.Visibility = Visibility.Visible;
        }

        private void ShowLog_Click(object sender, RoutedEventArgs e)
        {
            DeviceDataButton.Style = (Style)FindResource("ToggleButton");
            LogButton.Style = (Style)FindResource("ActiveToggleButton");
    
            HeaderIcon.Icon = FontAwesome.Sharp.IconChar.FileLines;
            HeaderText.Text = "로그";
    
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

                ServerStatusText.Text = "서버 중지됨";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.Red;

                Log("서버 중지됨");
                MessageBox.Show("서버가 중지되었습니다.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"서버 중지 실패: {ex.Message}");
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
                                Dispatcher.BeginInvoke(new Action(() => Log($"서버 실행 오류: {ex.Message}")));
                            }
                            Thread.Sleep(100);
                        }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.BeginInvoke(new Action(() => Log("서버 종료됨")));
            }
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            byte unitId;
            int startAddress;
            int count;

            if (!byte.TryParse(UnitIdTextBox.Text, out unitId))
            {
                MessageBox.Show("장치 ID를 0-255 사이의 값으로 입력하세요.");
                return;
            }

            if (slaveDevices.ContainsKey(unitId))
            {
                MessageBox.Show("이미 존재하는 장치입니다.");
                return;
            }

            if (!int.TryParse(StartAddressTextBox.Text, out startAddress) || startAddress < 0)
            {
                MessageBox.Show("올바른 시작 주소를 입력하세요. (0부터 시작)");
                return;
            }

            if (!int.TryParse(AddressCountTextBox.Text, out count) || count <= 0)
            {
                MessageBox.Show("올바른 주소 수를 입력하세요.");
                return;
            }

            ComboBoxItem selectedItem = RegisterTypeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null)
            {
                MessageBox.Show("레지스터 유형을 선택하세요.");
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

            Log($"장치 {unitId} 추가됨 (유형: {selectedItem.Content}, 시작주소: {startAddress}, 개수: {count})");
        }

        private void DeleteDevice_Click(object sender, RoutedEventArgs e)
        {
            TabItem selectedTab = DeviceTabControl.SelectedItem as TabItem;
            if (selectedTab != null && selectedTab.Tag is byte)
            {
                byte unitId = (byte)selectedTab.Tag;
                DeviceTabControl.Items.Remove(selectedTab);
                slaveDevices.Remove(unitId);
                customDataStore.RemoveDevice(unitId);
                Log($"장치 {unitId} 삭제됨");
            }
            else
            {
                MessageBox.Show("삭제할 장치를 선택하세요.");
            }
        }

        private UIElement CreateDeviceTab(ModbusSlaveDevice device)
        {
            Grid mainGrid = new Grid();
            mainGrid.Margin = new Thickness(4);

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
                string title = device.RegisterType == 40001 ? "🟠 Holding Register [40001+]" : "🟡 Input Register [30001+]";
                var card = CreateOptimizedRegisterCard(title, device.DualRegisters, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            return mainGrid;
        }

        // *** 최적화된 레지스터 카드 생성 - 가상화 지원 ***
        private UIElement CreateOptimizedRegisterCard(string title, ObservableCollection<DualRegisterModel> data, bool fillHeight = false)
        {
            Border cardBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Effect = new DropShadowEffect
                {
                    Color = Color.FromArgb(0x15, 0x00, 0x00, 0x00),
                    BlurRadius = 6,
                    ShadowDepth = 1,
                    Opacity = 0.2
                }
            };

            Grid cardContent = new Grid { Margin = new Thickness(12, 8, 12, 8) };
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 헤더
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 콘텐츠

            TextBlock header = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(44, 44, 44)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            // *** 가상화된 리스트박스 사용 ***
            ListBox virtualizedListBox = new ListBox();
            virtualizedListBox.ItemsSource = data;
            // 스크롤바 설정 수정
            ScrollViewer.SetVerticalScrollBarVisibility(virtualizedListBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(virtualizedListBox, ScrollBarVisibility.Disabled);
   
            // 가상화 설정
            VirtualizingStackPanel.SetIsVirtualizing(virtualizedListBox, true);
            VirtualizingStackPanel.SetVirtualizationMode(virtualizedListBox, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(virtualizedListBox, true);
            
            // 가상화 설정
            VirtualizingStackPanel.SetIsVirtualizing(virtualizedListBox, true);
            VirtualizingStackPanel.SetVirtualizationMode(virtualizedListBox, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(virtualizedListBox, true);

            // 항목 템플릿 설정
            virtualizedListBox.ItemTemplate = CreateOptimizedDataTemplate();
            
            // 항목 컨테이너 스타일
            virtualizedListBox.ItemContainerStyle = new Style(typeof(ListBoxItem));
            virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
            virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0)));
            virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(0)));
            virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent));
            
            // 선택 비활성화
            virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.FocusableProperty, false));
            var noSelectionTemplate = new ControlTemplate(typeof(ListBoxItem));
            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            noSelectionTemplate.VisualTree = contentPresenter;
            virtualizedListBox.ItemContainerStyle.Setters.Add(new Setter(ListBoxItem.TemplateProperty, noSelectionTemplate));

            Grid.SetRow(virtualizedListBox, 1);
            cardContent.Children.Add(virtualizedListBox);
            cardBorder.Child = cardContent;

            return cardBorder;
        }

        // *** 최적화된 데이터 템플릿 생성 ***
        // CreateOptimizedDataTemplate 메서드 수정:

        private DataTemplate CreateOptimizedDataTemplate()
        {
            DataTemplate template = new DataTemplate();
    
            FrameworkElementFactory borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(250, 251, 252)));
            borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(230, 230, 230)));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            borderFactory.SetValue(Border.MarginProperty, new Thickness(0, 2, 0, 2));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(12));

            FrameworkElementFactory stackFactory = new FrameworkElementFactory(typeof(StackPanel));
            stackFactory.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);


            FrameworkElementFactory controlFactory = new FrameworkElementFactory(typeof(OptimizedRegisterControl));
            controlFactory.SetBinding(OptimizedRegisterControl.RegisterModelProperty, new Binding("."));
            controlFactory.AddHandler(OptimizedRegisterControl.RegisterValueUpdatedEvent, new RoutedEventHandler(OnRegisterValueUpdated));

            stackFactory.AppendChild(controlFactory);
            borderFactory.AppendChild(stackFactory);
            template.VisualTree = borderFactory;

            return template;
        }

        // Coil용 카드 생성 (기존 방식 - 가상화 추가)
        private UIElement CreateCoilCard(string title, ObservableCollection<RegisterModel> data, bool fillHeight = false)
        {
            Border cardBorder = new Border();
            cardBorder.Background = Brushes.White;
            cardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(225, 225, 225));
            cardBorder.BorderThickness = new Thickness(1);
            cardBorder.CornerRadius = new CornerRadius(6);
            cardBorder.Margin = new Thickness(0, 0, 0, 4);
            cardBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            cardBorder.VerticalAlignment = VerticalAlignment.Stretch;
            cardBorder.Effect = new DropShadowEffect
            {
                Color = Color.FromArgb(0x15, 0x00, 0x00, 0x00),
                BlurRadius = 6,
                ShadowDepth = 1,
                Opacity = 0.2
            };

            Grid cardContent = new Grid();
            cardContent.Margin = new Thickness(12, 8, 12, 8);
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            TextBlock header = new TextBlock();
            header.Text = title;
            header.FontSize = 14;
            header.FontWeight = FontWeights.SemiBold;
            header.Foreground = new SolidColorBrush(Color.FromRgb(44, 44, 44));
            header.Margin = new Thickness(0, 0, 0, 8);
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
            grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            grid.RowHeight = 28;
            grid.FontSize = 12;
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
                new SolidColorBrush(Color.FromRgb(248, 249, 250))));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(92, 92, 92))));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty,
                FontWeights.Medium));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 12.0));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.HeightProperty, 30.0));

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Address",
                Binding = new Binding("DisplayAddress"),
                IsReadOnly = true,
                Width = new DataGridLength(0.3, DataGridLengthUnitType.Star),
                MinWidth = 80
            });

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Binding = new Binding("Value"),
                IsReadOnly = false,
                Width = new DataGridLength(0.35, DataGridLengthUnitType.Star),
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
                            Log($"레지스터 {register.DisplayAddress} 값이 {register.Value}로 변경됨");
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
                LogBox.Items.Add($"{DateTime.Now:HH:mm:ss} - {message}");
                if (LogBox.Items.Count > 100)
                    LogBox.Items.RemoveAt(0);
                LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
            }), DispatcherPriority.Background);
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

    // *** 최적화된 레지스터 컨트롤 (UserControl 기반) ***
    public class OptimizedRegisterControl : UserControl
    {
        public static readonly DependencyProperty RegisterModelProperty =
            DependencyProperty.Register("RegisterModel", typeof(DualRegisterModel), typeof(OptimizedRegisterControl),
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

        private static readonly Dictionary<int, OptimizedRegisterControl> activeControls = 
            new Dictionary<int, OptimizedRegisterControl>();

        public OptimizedRegisterControl()
        {
            CreateUI();
        }

        // CreateUI 메서드 수정:

        private void CreateUI()
        {
            Grid mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 헤더
            headerLabel = new TextBlock();
            headerLabel.FontWeight = FontWeights.SemiBold;
            headerLabel.FontSize = 13;
            headerLabel.Margin = new Thickness(0, 0, 0, 0);
            Grid.SetRow(headerLabel, 0);
            mainGrid.Children.Add(headerLabel);

            // 입력 컨트롤 행
            StackPanel inputRow = new StackPanel();
            inputRow.Orientation = Orientation.Horizontal;
            inputRow.VerticalAlignment = VerticalAlignment.Top; // 상단 정렬
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
            panel.Margin = new Thickness(0, 0, 10, 0);

            TextBlock label = new TextBlock();
            label.Text = "10진수: ";
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 5, 0);

            decimalTextBox = new TextBox();
            decimalTextBox.Width = 50;
            decimalTextBox.Height = 26;
            decimalTextBox.VerticalContentAlignment = VerticalAlignment.Center; 
            
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
            panel.Margin = new Thickness(0, 0, 10, 0);

            TextBlock label = new TextBlock();
            label.Text = "16진수: ";
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 5, 0);

            hexTextBox = new TextBox();
            hexTextBox.Width = 50;
            hexTextBox.Height = 26;
            hexTextBox.VerticalContentAlignment = VerticalAlignment.Center; 
            
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
            panel.Margin = new Thickness(0, 0, 20, 0);

            TextBlock label = new TextBlock();
            label.Text = "문자열: ";
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 5, 0);

            stringTextBox = new TextBox();
            stringTextBox.Width = 35;
            stringTextBox.Height = 26;
            stringTextBox.MaxLength = 2;
            stringTextBox.VerticalContentAlignment = VerticalAlignment.Center; 
            
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
            panel.Margin = new Thickness(0, 0, 20, 0);

            TextBlock label = new TextBlock();
            label.Text = "2진수: ";
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 5, 0);

            binaryTextBox = new TextBox();
            binaryTextBox.IsReadOnly = true;
            binaryTextBox.Background = new SolidColorBrush(Color.FromRgb(248, 249, 250));
            binaryTextBox.FontFamily = new FontFamily("Consolas");
            binaryTextBox.FontSize = 13;
            binaryTextBox.Width = 130;
            binaryTextBox.Height = 26;
            binaryTextBox.VerticalContentAlignment = VerticalAlignment.Center; 

            panel.Children.Add(label);
            panel.Children.Add(binaryTextBox);
            parent.Children.Add(panel);
        }

        // CreateBitEditor 메서드 수정:

private void CreateBitEditor(StackPanel parent)
{
   StackPanel bitSection = new StackPanel();
   bitSection.Orientation = Orientation.Horizontal;

   TextBlock label = new TextBlock();
   label.Text = "비트: ";
   label.VerticalAlignment = VerticalAlignment.Center;
   label.Margin = new Thickness(0, 0, 5, 0);

   bitGrid = new Grid();
   bitGrid.HorizontalAlignment = HorizontalAlignment.Left;

   // 행 정의 추가
   bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 비트 번호
   bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 비트 값

   // 16개 열 정의
   for (int i = 0; i < 16; i++)
   {
       bitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
   }

   // 비트 번호 라벨 생성 (15부터 0까지)
   for (int bit = 15; bit >= 0; bit--)
   {
       TextBlock bitNumberLabel = new TextBlock();
       bitNumberLabel.Text = bit.ToString();
       bitNumberLabel.FontSize = 8;
       bitNumberLabel.FontWeight = FontWeights.Bold;
       bitNumberLabel.Foreground = Brushes.DarkBlue;
       bitNumberLabel.TextAlignment = TextAlignment.Center;
       bitNumberLabel.HorizontalAlignment = HorizontalAlignment.Center;
       bitNumberLabel.Margin = new Thickness(1, 0, 1, 0);
       
       Grid.SetRow(bitNumberLabel, 0);
       Grid.SetColumn(bitNumberLabel, 15 - bit);
       bitGrid.Children.Add(bitNumberLabel);
   }

   // 비트 텍스트박스 생성 (15부터 0까지)
   for (int bit = 15; bit >= 0; bit--)
   {
       TextBox bitTextBox = new TextBox();
       bitTextBox.Width = 18;
       bitTextBox.Height = 25;
       bitTextBox.FontSize = 9;
       bitTextBox.FontWeight = FontWeights.Bold;
       bitTextBox.TextAlignment = TextAlignment.Center;
       bitTextBox.VerticalContentAlignment = VerticalAlignment.Center;
       bitTextBox.MaxLength = 1;
       bitTextBox.Tag = bit;
       bitTextBox.IsTabStop = false;
       bitTextBox.Margin = new Thickness(0, 0, 0, 10);

       bitTextBox.TextChanged += BitTextBox_TextChanged;
       bitTextBox.KeyDown += BitTextBox_KeyDown;
       bitTextBox.GotFocus += (s, e) => ((TextBox)s).SelectAll();
       
       bitTextBox.MouseDoubleClick += (sender, e) =>
       {
           var tb = sender as TextBox;
           string newValue = (tb.Text == "0") ? "1" : "0";
           tb.Text = newValue;
       };

       Grid.SetRow(bitTextBox, 1); // 두 번째 행에 배치
       Grid.SetColumn(bitTextBox, 15 - bit);
       bitGrid.Children.Add(bitTextBox);
   }

   bitSection.Children.Add(label);
   bitSection.Children.Add(bitGrid);
   parent.Children.Add(bitSection);
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
                // 비트 간 이동 처리
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
                textBox.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                textBox.Foreground = Brushes.White;
            }
            else
            {
                textBox.Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                textBox.Foreground = Brushes.Black;
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
                // 헤더 업데이트
                headerLabel.Text = $"Register {RegisterModel.DisplayAddress} (Address: {RegisterModel.ModbusAddress}) - Value: {RegisterModel.RegisterValue}";
                
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
            "RegisterValueUpdated", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(OptimizedRegisterControl));

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
            var control = d as OptimizedRegisterControl;
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