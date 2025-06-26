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
using Modbus.Data;

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

        public MainWindow()
        {
            InitializeComponent();
            customDataStore = new CustomDataStore();

            StartAddressTextBox.ToolTip = "시작 주소 오프셋 (0부터 시작)\n" +
                                          "예: 0 입력 시 30001부터, 10 입력 시 30011부터";
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
                                Dispatcher.Invoke(() => Log($"서버 실행 오류: {ex.Message}"));
                            }
                            Thread.Sleep(100);
                        }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => Log("서버 종료됨"));
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
                var card = CreateDualRegisterCard(title, device.DualRegisters, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            return mainGrid;
        }

        // 듀얼 입력 레지스터 카드 생성 (비트/바이트 모드 선택 가능)
        private UIElement CreateDualRegisterCard(string title, ObservableCollection<DualRegisterModel> data, bool fillHeight = false)
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
    cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 모드 선택
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

    StackPanel modePanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 0, 0, 10)
    };
    Grid.SetRow(modePanel, 1);

    TextBlock modeLabel = new TextBlock
    {
        Text = "입력 모드 : 2바이트 기준",
        FontWeight = FontWeights.Medium,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0)
    };
    modePanel.Children.Add(modeLabel);

    RadioButton byteModeRadio = new RadioButton
    {
        Content = "10진수",
        IsChecked = true,
        GroupName = $"InputMode_{title}",
        Margin = new Thickness(0, 0, 15, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    modePanel.Children.Add(byteModeRadio);

    RadioButton bitModeRadio = new RadioButton
    {
        Content = "2진수",
        GroupName = $"InputMode_{title}",
        Margin = new Thickness(0, 0, 15, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    modePanel.Children.Add(bitModeRadio);

    cardContent.Children.Add(modePanel);

    Border contentContainer = new Border();
    Grid.SetRow(contentContainer, 2);

    ScrollViewer scrollViewer = new ScrollViewer
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    StackPanel registerStack = new StackPanel { Orientation = Orientation.Vertical };
    Dictionary<DualRegisterModel, FrameworkElement> registerPanels = new Dictionary<DualRegisterModel, FrameworkElement>();

    foreach (var dualRegister in data)
    {
        var panel = CreateDualRegisterPanel(dualRegister, byteModeRadio, bitModeRadio);
        registerStack.Children.Add(panel);
        registerPanels[dualRegister] = panel;
    }

    // 바이트 모드 전환 시 UI 갱신
    byteModeRadio.Checked += (s, e) =>
    {
        foreach (var kvp in registerPanels)
        {
            UpdateRegisterPanelMode(kvp.Value, true); // 바이트 모드
        }
    };

    // 비트 모드 전환 시 UI 갱신 + 레지스터 값 → 비트 텍스트박스 수동 적용
    bitModeRadio.Checked += (s, e) =>
    {
        foreach (var kvp in registerPanels)
        {
            var panel = kvp.Value;
            var tag = panel.Tag;
            var bitPanel = tag.GetType().GetProperty("BitPanel")?.GetValue(tag) as StackPanel;

            UpdateRegisterPanelMode(panel, false); // 비트 모드

            if (bitPanel != null)
            {
                var grid = bitPanel.Children.OfType<Grid>().FirstOrDefault();
                if (grid != null)
                {
                    for (int bit = 0; bit <= 15; bit++)
                    {
                        int bitValue = (kvp.Key.RegisterValue >> bit) & 1;
                        var tb = grid.Children.OfType<TextBox>().FirstOrDefault(t => (int)t.Tag == bit);
                        if (tb != null)
                        {
                            tb.Text = bitValue.ToString();
                            UpdateBitTextBoxAppearance(tb, bitValue);
                        }
                    }
                }
            }
        }
    };

    scrollViewer.Content = registerStack;
    contentContainer.Child = scrollViewer;
    cardContent.Children.Add(contentContainer);
    cardBorder.Child = cardContent;

    return cardBorder;
}


        // 듀얼 레지스터 패널 생성 (바이트/비트 모드 전환 가능)
        private FrameworkElement CreateDualRegisterPanel(DualRegisterModel dualRegister, RadioButton byteMode, RadioButton bitMode)
        {
            Border border = new Border();
            border.Background = new SolidColorBrush(Color.FromRgb(250, 251, 252));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230));
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(4);
            border.Margin = new Thickness(0, 2, 0, 2);
            border.Padding = new Thickness(8);

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 주소 정보
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 입력 영역

            // 레지스터 정보
            TextBlock addressLabel = new TextBlock();
            addressLabel.Text = $"Register {dualRegister.DisplayAddress} (Number : {dualRegister.ModbusAddress}) - Value : {dualRegister.RegisterValue}";
            addressLabel.FontWeight = FontWeights.Medium;
            addressLabel.FontSize = 12;
            addressLabel.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(addressLabel, 0);
            grid.Children.Add(addressLabel);

            // 입력 영역 컨테이너
            Border inputContainer = new Border();
            Grid.SetRow(inputContainer, 1);

            // 바이트 입력 패널
            StackPanel byteInputPanel = CreateByteInputPanel(dualRegister, addressLabel);
            byteInputPanel.Tag = "ByteInput";

            // 비트 입력 패널  
            StackPanel bitInputPanel = CreateBitInputPanel(dualRegister, addressLabel);
            bitInputPanel.Tag = "BitInput";
            bitInputPanel.Visibility = Visibility.Collapsed;

            // 컨테이너에 두 패널 모두 추가
            Grid inputGrid = new Grid();
            inputGrid.Children.Add(byteInputPanel);
            inputGrid.Children.Add(bitInputPanel);

            inputContainer.Child = inputGrid;
            grid.Children.Add(inputContainer);

            // 패널에 모드 전환 정보 저장
            border.Tag = new { BytePanel = byteInputPanel, BitPanel = bitInputPanel, AddressLabel = addressLabel };

            border.Child = grid;
            return border;
        }

        private StackPanel CreateByteInputPanel(DualRegisterModel dualRegister, TextBlock addressLabel)
{
    StackPanel panel = new StackPanel();
    panel.Orientation = Orientation.Horizontal;
    panel.HorizontalAlignment = HorizontalAlignment.Left;

    TextBlock valueLabel = new TextBlock();
    valueLabel.Text = "값: ";
    valueLabel.VerticalAlignment = VerticalAlignment.Center;
    valueLabel.Margin = new Thickness(0, 0, 8, 0);
    panel.Children.Add(valueLabel);

    TextBox valueTextBox = new TextBox();
    valueTextBox.Width = 100;
    valueTextBox.Height = 25;
    valueTextBox.Text = dualRegister.RegisterValue.ToString();
    valueTextBox.VerticalAlignment = VerticalAlignment.Center;
    valueTextBox.Margin = new Thickness(0, 0, 15, 0);

    TextBlock hexLabel = new TextBlock();
    hexLabel.Text = $"(0x{dualRegister.RegisterValue:X4})";
    hexLabel.VerticalAlignment = VerticalAlignment.Center;
    hexLabel.Foreground = Brushes.Gray;
    hexLabel.FontSize = 10;

    TextBlock binaryLabel = new TextBlock();
    binaryLabel.Text = $"({Convert.ToString(dualRegister.RegisterValue & 0xFFFF, 2).PadLeft(16, '0')})";
    binaryLabel.VerticalAlignment = VerticalAlignment.Center;
    binaryLabel.Foreground = Brushes.Blue;
    binaryLabel.FontSize = 10;
    binaryLabel.FontFamily = new FontFamily("Consolas");
    binaryLabel.Margin = new Thickness(10, 0, 0, 0);

    bool isExternalUpdate = false;

    Action processInput = () =>
    {
        string inputText = valueTextBox.Text.Trim();

        if (string.IsNullOrEmpty(inputText))
        {
            inputText = "0";
            valueTextBox.Text = "0";
        }

        if (int.TryParse(inputText, out int value))
        {
            if (value < 0) value = 0;
            if (value > 65535) value = 65535;

            if (value != dualRegister.RegisterValue)
            {
                isExternalUpdate = true;
                dualRegister.RegisterValue = value;

                hexLabel.Text = $"(0x{value:X4})";
                binaryLabel.Text = $"({Convert.ToString(value & 0xFFFF, 2).PadLeft(16, '0')})";
                addressLabel.Text = $"Register {dualRegister.DisplayAddress} (Protocol: {dualRegister.ModbusAddress}) - Value: {value}";
                valueTextBox.Text = value.ToString();

                // 즉시 비트 UI 업데이트
                UpdateBitUIIfInBitMode(dualRegister);
                
                UpdateCurrentDeviceDataStore();
                isExternalUpdate = false;
            }
        }
        else
        {
            isExternalUpdate = true;
            valueTextBox.Text = dualRegister.RegisterValue.ToString();
            isExternalUpdate = false;
        }
    };

    valueTextBox.KeyDown += (sender, e) =>
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            processInput();
            e.Handled = true;
        }
    };

    valueTextBox.LostFocus += (sender, e) =>
    {
        processInput();
    };

    dualRegister.PropertyChanged += (sender, e) =>
    {
        if (e.PropertyName == nameof(DualRegisterModel.RegisterValue) && !isExternalUpdate)
        {
            if (!valueTextBox.IsFocused)
            {
                isExternalUpdate = true;
                valueTextBox.Text = dualRegister.RegisterValue.ToString();
                isExternalUpdate = false;
            }

            hexLabel.Text = $"(0x{dualRegister.RegisterValue:X4})";
            binaryLabel.Text = $"({Convert.ToString(dualRegister.RegisterValue & 0xFFFF, 2).PadLeft(16, '0')})";
            addressLabel.Text = $"Register {dualRegister.DisplayAddress} (Protocol: {dualRegister.ModbusAddress}) - Value: {dualRegister.RegisterValue}";

            // PropertyChanged를 통한 업데이트에서도 비트 UI 업데이트
            UpdateBitUIIfInBitMode(dualRegister);
        }
    };

    panel.Children.Add(valueTextBox);
    panel.Children.Add(hexLabel);
    panel.Children.Add(binaryLabel);

    return panel;
}

// MainWindow 클래스에 추가할 메서드
private void UpdateBitUIIfInBitMode(DualRegisterModel dualRegister)
{
    System.Diagnostics.Debug.WriteLine($"UpdateBitUIIfInBitMode 호출됨 - RegisterValue: {dualRegister.RegisterValue}");
    
    // 현재 선택된 탭에서 해당 레지스터의 비트 패널 찾기
    TabItem selectedTab = DeviceTabControl.SelectedItem as TabItem;
    if (selectedTab?.Content is Grid mainGrid)
    {
        System.Diagnostics.Debug.WriteLine("MainGrid 찾음");
        
        foreach (var child in mainGrid.Children)
        {
            if (child is Border cardBorder && cardBorder.Child is Grid cardContent)
            {
                System.Diagnostics.Debug.WriteLine("CardBorder 찾음");
                
                // 비트 모드 라디오 버튼 찾기
                var modePanel = cardContent.Children.OfType<StackPanel>()
                    .FirstOrDefault(sp => sp.Children.OfType<RadioButton>().Any());
                
                if (modePanel != null)
                {
                    var bitModeRadio = modePanel.Children.OfType<RadioButton>()
                        .FirstOrDefault(rb => rb.Content.ToString() == "개별 비트");
                    
                    System.Diagnostics.Debug.WriteLine($"비트모드 라디오 상태: {bitModeRadio?.IsChecked}");
                    
                    // 비트 모드가 선택된 경우에만 비트 UI 업데이트
                    if (bitModeRadio?.IsChecked == true)
                    {
                        System.Diagnostics.Debug.WriteLine("비트 모드 활성화됨");
                        
                        // 레지스터 패널들 찾기
                        var contentContainer = cardContent.Children.OfType<Border>().LastOrDefault();
                        if (contentContainer?.Child is ScrollViewer scrollViewer && 
                            scrollViewer.Content is StackPanel registerStack)
                        {
                            System.Diagnostics.Debug.WriteLine($"RegisterStack 찾음, 패널 수: {registerStack.Children.Count}");
                            
                            foreach (var registerPanel in registerStack.Children.OfType<Border>())
                            {
                                var tag = registerPanel.Tag;
                                if (tag != null)
                                {
                                    var bitPanel = tag.GetType().GetProperty("BitPanel")?.GetValue(tag) as StackPanel;
                                    System.Diagnostics.Debug.WriteLine($"BitPanel 가시성: {bitPanel?.Visibility}");
                                    
                                    if (bitPanel?.Visibility == Visibility.Visible)
                                    {
                                        // 비트 그리드 찾기
                                        var bitGrid = bitPanel.Children.OfType<Grid>().FirstOrDefault();
                                        if (bitGrid != null)
                                        {
                                            // 해당 레지스터인지 확인 후 비트 텍스트박스들 업데이트
                                            var addressLabel = tag.GetType().GetProperty("AddressLabel")?.GetValue(tag) as TextBlock;
                                            System.Diagnostics.Debug.WriteLine($"AddressLabel 텍스트: {addressLabel?.Text}");
                                            System.Diagnostics.Debug.WriteLine($"찾는 레지스터: Register {dualRegister.DisplayAddress}");
                                            
                                            if (addressLabel?.Text.Contains($"Register {dualRegister.DisplayAddress}") == true)
                                            {
                                                System.Diagnostics.Debug.WriteLine("해당 레지스터 발견! 비트 업데이트 시작");
                                                
                                                for (int bit = 0; bit <= 15; bit++)
                                                {
                                                    int bitValue = (dualRegister.RegisterValue >> bit) & 1;
                                                    var tb = bitGrid.Children.OfType<TextBox>()
                                                        .FirstOrDefault(t => (int)t.Tag == bit);
                                                    if (tb != null)
                                                    {
                                                        System.Diagnostics.Debug.WriteLine($"비트 {bit}: {tb.Text} -> {bitValue}");
                                                        // 항상 업데이트 (조건 제거)
                                                        tb.Text = bitValue.ToString();
                                                        UpdateBitTextBoxAppearance(tb, bitValue);
                                                    }
                                                }
                                                System.Diagnostics.Debug.WriteLine("비트 업데이트 완료");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    else
    {
        System.Diagnostics.Debug.WriteLine("MainGrid를 찾을 수 없음");
    }
}

        // 비트 입력 패널 생성 (내림차순 15-0) - Grid 레이아웃으로 정확한 정렬
        private StackPanel CreateBitInputPanel(DualRegisterModel dualRegister, TextBlock addressLabel)
        {
            StackPanel mainPanel = new StackPanel();
            mainPanel.Orientation = Orientation.Vertical;

            // Grid를 사용하여 정확한 정렬
            Grid bitGrid = new Grid();
            bitGrid.HorizontalAlignment = HorizontalAlignment.Left;

            // 행 정의 (비트 번호, 입력 박스)
            bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 비트 번호
            bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 입력 박스

            // 열 정의 (16개 비트용)
            for (int i = 0; i <= 15; i++) // 0부터 15까지 16개 열 생성
            {
                bitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
            }

            // 비트 번호 생성 (15부터 0까지)
            for (int bit = 15; bit >= 0; bit--) // 내림차순으로 변경
            {
                TextBlock bitNumber = new TextBlock();
                bitNumber.Text = bit.ToString();
                bitNumber.FontSize = 8;
                bitNumber.FontWeight = FontWeights.Bold;
                bitNumber.Foreground = Brushes.DarkBlue;
                bitNumber.TextAlignment = TextAlignment.Center;
                bitNumber.VerticalAlignment = VerticalAlignment.Center;
                bitNumber.Margin = new Thickness(1, 0, 1, 2);
                
                Grid.SetRow(bitNumber, 0);
                Grid.SetColumn(bitNumber, 15 - bit); // 열 순서 조정 (15 -> 0열, 0 -> 15열)
                bitGrid.Children.Add(bitNumber);
            }

            // 비트 입력 텍스트박스 생성 (15부터 0까지)
            for (int bit = 15; bit >= 0; bit--) // 내림차순으로 변경
            {
                TextBox bitTextBox = new TextBox();
                bitTextBox.Width = 25;
                bitTextBox.Height = 25;
                bitTextBox.FontSize = 11;
                bitTextBox.FontWeight = FontWeights.Bold;
                bitTextBox.TextAlignment = TextAlignment.Center;
                bitTextBox.VerticalContentAlignment = VerticalAlignment.Center;
                bitTextBox.MaxLength = 1;
                bitTextBox.Tag = bit; // 비트 위치는 그대로 0-15 유지

                // 현재 레지스터 값에서 해당 비트 추출
                int bitValue = (dualRegister.RegisterValue >> bit) & 1;
                bitTextBox.Text = bitValue.ToString();

                // 비트 값에 따라 색상 변경
                UpdateBitTextBoxAppearance(bitTextBox, bitValue);

                // 텍스트 변경 이벤트
                bitTextBox.TextChanged += (sender, e) =>
                {
                    var tb = sender as TextBox;
                    if (tb.Text == "0" || tb.Text == "1")
                    {
                        int newBitValue = int.Parse(tb.Text);
                        UpdateBitTextBoxAppearance(tb, newBitValue);

                        // 비트 변경 후 레지스터 업데이트
                        UpdateRegisterFromBits(dualRegister, bitGrid);
                        addressLabel.Text = $"Register {dualRegister.DisplayAddress} (Protocol: {dualRegister.ModbusAddress}) - Value: {dualRegister.RegisterValue}";
                        UpdateCurrentDeviceDataStore();
                    }
                    else if (!string.IsNullOrEmpty(tb.Text))
                    {
                        tb.Text = "0";
                        UpdateBitTextBoxAppearance(tb, 0);
                    }
                };

                // 포커스 시 전체 선택
                bitTextBox.GotFocus += (sender, e) =>
                {
                    var tb = sender as TextBox;
                    tb.SelectAll();
                };

                // 더블클릭으로 비트 토글
                bitTextBox.MouseDoubleClick += (sender, e) =>
                {
                    var tb = sender as TextBox;
                    int currentValue = int.TryParse(tb.Text, out int val) ? val : 0;
                    tb.Text = (currentValue == 0) ? "1" : "0";
                };

                Grid.SetRow(bitTextBox, 1);
                Grid.SetColumn(bitTextBox, 15 - bit); // 열 순서 조정 (15 -> 0열, 0 -> 15열)
                bitGrid.Children.Add(bitTextBox);
            }

            // 레지스터 값 변경 시 비트 UI 업데이트
            dualRegister.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(DualRegisterModel.RegisterValue))
                {
                    for (int bit = 15; bit >= 0; bit--) // 내림차순으로 변경
                    {
                        int bitValue = (dualRegister.RegisterValue >> bit) & 1;
                        var textBox = bitGrid.Children.OfType<TextBox>().FirstOrDefault(tb => (int)tb.Tag == bit);
                        if (textBox != null && textBox.Text != bitValue.ToString())
                        {
                            textBox.Text = bitValue.ToString();
                            UpdateBitTextBoxAppearance(textBox, bitValue);
                        }
                    }
                }
            };

            mainPanel.Children.Add(bitGrid);

            return mainPanel;
        }

        // 비트 텍스트박스 외관 업데이트
        private void UpdateBitTextBoxAppearance(TextBox textBox, int bitValue)
        {
            if (bitValue == 1)
            {
                textBox.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 초록색
                textBox.Foreground = Brushes.White;
                textBox.FontWeight = FontWeights.Bold;
            }
            else
            {
                textBox.Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)); // 회색
                textBox.Foreground = Brushes.Black;
                textBox.FontWeight = FontWeights.Normal;
            }
        }

        // 레지스터 패널 모드 업데이트
        private void UpdateRegisterPanelMode(FrameworkElement panel, bool isByteMode)
        {
            if (panel.Tag is System.Dynamic.ExpandoObject)
                return;

            var tagData = panel.Tag;
            var bytePanel = tagData.GetType().GetProperty("BytePanel")?.GetValue(tagData) as StackPanel;
            var bitPanel = tagData.GetType().GetProperty("BitPanel")?.GetValue(tagData) as StackPanel;

            if (bytePanel != null && bitPanel != null)
            {
                if (isByteMode)
                {
                    bytePanel.Visibility = Visibility.Visible;
                    bitPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    bytePanel.Visibility = Visibility.Collapsed;
                    bitPanel.Visibility = Visibility.Visible;
                }
            }
        }

        // 비트에서 레지스터 값 업데이트 (Grid 버전)
        private void UpdateRegisterFromBits(DualRegisterModel dualRegister, Grid bitGrid)
        {
            int newValue = 0;

            // 비트 패널의 텍스트박스들을 순회하며 레지스터 값 계산
            foreach (TextBox textBox in bitGrid.Children.OfType<TextBox>())
            {
                if (int.TryParse(textBox.Text, out int bitValue) && (bitValue == 0 || bitValue == 1))
                {
                    int bitPosition = (int)textBox.Tag; // Tag에 저장된 비트 위치 사용
                    if (bitValue == 1)
                    {
                        newValue |= (1 << bitPosition);
                    }
                }
            }

            dualRegister.RegisterValue = newValue;
        }

        // Coil용 카드 생성 (기존 방식)
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
                        }), System.Windows.Threading.DispatcherPriority.Background);
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
            Dispatcher.Invoke(() =>
            {
                LogBox.Items.Add($"{DateTime.Now:HH:mm:ss} - {message}");
                if (LogBox.Items.Count > 100)
                    LogBox.Items.RemoveAt(0);
                LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
            });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (isServerRunning)
            {
                StopServer_Click(null, null);
            }
            base.OnClosing(e);
        }
    }

    // 기존 RegisterModel (Coil용)
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
                    int oldValue = _value;
                    _value = value;
                    OnPropertyChanged(nameof(Value));
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                    System.Diagnostics.Debug.WriteLine($"RegisterModel 값 변경 - Display:{DisplayAddress} Modbus:{ModbusAddress} : {oldValue} -> {value}");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler ValueChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // 듀얼 입력 레지스터 모델 (바이트/비트 모드 지원)
    public class DualRegisterModel : INotifyPropertyChanged
    {
        private int _registerValue;
        public int DisplayAddress { get; set; }     // 40001, 30001 등
        public int ModbusAddress { get; set; }      // 0-based 주소

        public int RegisterValue
        {
            get => _registerValue;
            set
            {
                if (_registerValue != value)
                {
                    int oldValue = _registerValue;
                    _registerValue = Math.Max(0, Math.Min(65535, value)); // 0-65535 범위 제한
                    OnPropertyChanged(nameof(RegisterValue));
                    RegisterValueChanged?.Invoke(this, EventArgs.Empty);
                    System.Diagnostics.Debug.WriteLine($"DualRegisterModel 값 변경 - Display:{DisplayAddress} Modbus:{ModbusAddress} : {oldValue} -> {_registerValue}");
                }
            }
        }

        // 개별 비트 접근 속성들 (0-15)
        public int GetBit(int bitPosition)
        {
            return (_registerValue >> bitPosition) & 1;
        }

        public void SetBit(int bitPosition, int value)
        {
            if (value == 1)
            {
                RegisterValue |= (1 << bitPosition);
            }
            else
            {
                RegisterValue &= ~(1 << bitPosition);
            }
        }

        // 바이트 접근 속성들
        public byte LowByte
        {
            get => (byte)(_registerValue & 0xFF);
            set => RegisterValue = (_registerValue & 0xFF00) | value;
        }

        public byte HighByte
        {
            get => (byte)((_registerValue >> 8) & 0xFF);
            set => RegisterValue = (_registerValue & 0x00FF) | (value << 8);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler RegisterValueChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // ModbusSlaveDevice 클래스 (듀얼 레지스터 지원)
    public class ModbusSlaveDevice
    {
        public byte UnitId { get; private set; }
        public int RegisterType { get; private set; } // 30001 또는 40001

        public ObservableCollection<RegisterModel> Coils;
        public ObservableCollection<RegisterModel> DiscreteInputs;
        public ObservableCollection<DualRegisterModel> DualRegisters; // 듀얼 모드 레지스터

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
            ObservableCollection<RegisterModel> list = new ObservableCollection<RegisterModel>();

            for (int i = 0; i < count; i++)
            {
                var register = new RegisterModel
                {
                    DisplayAddress = baseAddr + startAddr + i,
                    ModbusAddress = startAddr + i,
                    Value = 0
                };

                register.ValueChanged += (sender, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"Coil Register {register.DisplayAddress} (Protocol Addr: {register.ModbusAddress}) changed to {register.Value}");
                };

                list.Add(register);
            }
            return list;
        }

        private ObservableCollection<DualRegisterModel> CreateDualRegisters(int startAddr, int count, int baseAddr)
        {
            ObservableCollection<DualRegisterModel> list = new ObservableCollection<DualRegisterModel>();

            for (int i = 0; i < count; i++)
            {
                var register = new DualRegisterModel
                {
                    DisplayAddress = baseAddr + startAddr + i,
                    ModbusAddress = startAddr + i,
                    RegisterValue = 0
                };

                register.RegisterValueChanged += (sender, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"Dual Register {register.DisplayAddress} (Protocol Addr: {register.ModbusAddress}) changed to {register.RegisterValue}");
                };

                list.Add(register);
            }
            return list;
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

    // CustomDataStore 클래스
    public class CustomDataStore : DataStore
    {
        private Dictionary<byte, ModbusSlaveDevice> devices = new Dictionary<byte, ModbusSlaveDevice>();
        private Dictionary<byte, DeviceDataCache> deviceDataCache = new Dictionary<byte, DeviceDataCache>();
        private bool isUpdatingFromMaster = false;

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

                System.Diagnostics.Debug.WriteLine($"DataStore 초기화 완료 - 각 타입별 {1000}개 요소");
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
            System.Diagnostics.Debug.WriteLine($"장치 {unitId} 추가 및 캐시 생성 완료");
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
            System.Diagnostics.Debug.WriteLine($"=== 장치 {unitId} 데이터를 DataStore에 로드 시작 ===");

            ClearDataStore();

            // HoldingRegisters 로드
            foreach (var kvp in cache.HoldingRegisters)
            {
                int address = kvp.Key;
                ushort value = kvp.Value;

                try
                {
                    int dataStoreIndex = address + 1;

                    if (dataStoreIndex >= 0 && dataStoreIndex < HoldingRegisters.Count)
                    {
                        HoldingRegisters.RemoveAt(dataStoreIndex);
                        HoldingRegisters.Insert(dataStoreIndex, value);
                        System.Diagnostics.Debug.WriteLine($"캐시 → DataStore: HoldingRegister[{address}] → 인덱스[{dataStoreIndex}] = {value}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"HoldingRegister[{address}] 설정 오류: {ex.Message}");
                }
            }

            // InputRegisters 로드
            foreach (var kvp in cache.InputRegisters)
            {
                int address = kvp.Key;
                ushort value = kvp.Value;

                try
                {
                    int dataStoreIndex = address + 1;

                    if (dataStoreIndex >= 0 && dataStoreIndex < InputRegisters.Count)
                    {
                        InputRegisters.RemoveAt(dataStoreIndex);
                        InputRegisters.Insert(dataStoreIndex, value);
                        System.Diagnostics.Debug.WriteLine($"캐시 → DataStore: InputRegister[{address}] → 인덱스[{dataStoreIndex}] = {value}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"InputRegister[{address}] 설정 오류: {ex.Message}");
                }
            }

            // CoilDiscretes 로드
            foreach (var kvp in cache.CoilDiscretes)
            {
                int address = kvp.Key;
                bool value = kvp.Value;

                try
                {
                    int dataStoreIndex = address + 1;

                    if (dataStoreIndex >= 0 && dataStoreIndex < CoilDiscretes.Count)
                    {
                        CoilDiscretes.RemoveAt(dataStoreIndex);
                        CoilDiscretes.Insert(dataStoreIndex, value);
                        System.Diagnostics.Debug.WriteLine($"캐시 → DataStore: Coil[{address}] → 인덱스[{dataStoreIndex}] = {value}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Coil[{address}] 설정 오류: {ex.Message}");
                }
            }

            // InputDiscretes 로드
            foreach (var kvp in cache.InputDiscretes)
            {
                int address = kvp.Key;
                bool value = kvp.Value;

                try
                {
                    int dataStoreIndex = address + 1;

                    if (dataStoreIndex >= 0 && dataStoreIndex < InputDiscretes.Count)
                    {
                        InputDiscretes.RemoveAt(dataStoreIndex);
                        InputDiscretes.Insert(dataStoreIndex, value);
                        System.Diagnostics.Debug.WriteLine($"캐시 → DataStore: DiscreteInput[{address}] → 인덱스[{dataStoreIndex}] = {value}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DiscreteInput[{address}] 설정 오류: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"=== 장치 {unitId} 데이터 로드 완료 ===");
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

            // 듀얼 레지스터 처리
            if (device.DualRegisters != null)
            {
                foreach (var reg in device.DualRegisters)
                {
                    ushort value = (ushort)Math.Max(0, Math.Min(65535, reg.RegisterValue));

                    if (device.RegisterType == 40001) // Holding Register
                    {
                        cache.HoldingRegisters[reg.ModbusAddress] = value;
                        System.Diagnostics.Debug.WriteLine($"UI → 캐시: 장치{unitId} HoldingRegister[{reg.ModbusAddress}] = {value}");
                    }
                    else if (device.RegisterType == 30001) // Input Register
                    {
                        cache.InputRegisters[reg.ModbusAddress] = value;
                        System.Diagnostics.Debug.WriteLine($"UI → 캐시: 장치{unitId} InputRegister[{reg.ModbusAddress}] = {value}");
                    }
                }
            }

            // Coil 처리
            if (device.Coils != null)
            {
                foreach (var reg in device.Coils)
                {
                    bool value = reg.Value != 0;
                    cache.CoilDiscretes[reg.ModbusAddress] = value;
                    System.Diagnostics.Debug.WriteLine($"UI → 캐시: 장치{unitId} Coil[{reg.ModbusAddress}] = {value}");
                }
            }

            // Discrete Input 처리
            if (device.DiscreteInputs != null)
            {
                foreach (var reg in device.DiscreteInputs)
                {
                    bool value = reg.Value != 0;
                    cache.InputDiscretes[reg.ModbusAddress] = value;
                    System.Diagnostics.Debug.WriteLine($"UI → 캐시: 장치{unitId} DiscreteInput[{reg.ModbusAddress}] = {value}");
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
                System.Diagnostics.Debug.WriteLine($"마스터 쓰기 완료 - 시작주소: {e.StartAddress}, 타입: {e.ModbusDataType}");
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

                if (!deviceDataCache.ContainsKey(unitId))
                    continue;

                var cache = deviceDataCache[unitId];

                if (e.ModbusDataType == ModbusDataType.HoldingRegister && device.DualRegisters != null && device.RegisterType == 40001)
                {
                    var targetRegister = device.DualRegisters.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                    if (targetRegister != null)
                    {
                        int dataStoreIndex = e.StartAddress + 1;
                        if (dataStoreIndex < HoldingRegisters.Count)
                        {
                            ushort newValue = HoldingRegisters[dataStoreIndex];
                            cache.HoldingRegisters[e.StartAddress] = newValue;
                            System.Diagnostics.Debug.WriteLine($"마스터 쓰기 → 장치{unitId} 캐시: HoldingRegister[{e.StartAddress}] = {newValue}");
                        }
                    }
                }
                else if (e.ModbusDataType == ModbusDataType.InputRegister && device.DualRegisters != null && device.RegisterType == 30001)
                {
                    var targetRegister = device.DualRegisters.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                    if (targetRegister != null)
                    {
                        int dataStoreIndex = e.StartAddress + 1;
                        if (dataStoreIndex < InputRegisters.Count)
                        {
                            ushort newValue = InputRegisters[dataStoreIndex];
                            cache.InputRegisters[e.StartAddress] = newValue;
                            System.Diagnostics.Debug.WriteLine($"마스터 쓰기 → 장치{unitId} 캐시: InputRegister[{e.StartAddress}] = {newValue}");
                        }
                    }
                }
                else if (e.ModbusDataType == ModbusDataType.Coil && device.Coils != null)
                {
                    var targetRegister = device.Coils.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                    if (targetRegister != null)
                    {
                        int dataStoreIndex = e.StartAddress + 1;
                        if (dataStoreIndex < CoilDiscretes.Count)
                        {
                            bool newValue = CoilDiscretes[dataStoreIndex];
                            cache.CoilDiscretes[e.StartAddress] = newValue;
                            System.Diagnostics.Debug.WriteLine($"마스터 쓰기 → 장치{unitId} 캐시: Coil[{e.StartAddress}] = {newValue}");
                        }
                    }
                }
            }
        }

        private void UpdateCurrentDeviceUI(DataStoreEventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
                    if (mainWindow?.DeviceTabControl?.SelectedItem is TabItem selectedTab && selectedTab.Tag is byte currentUnitId)
                    {
                        if (devices.ContainsKey(currentUnitId))
                        {
                            var device = devices[currentUnitId];

                            if (e.ModbusDataType == ModbusDataType.HoldingRegister && device.DualRegisters != null && device.RegisterType == 40001)
                            {
                                var targetRegister = device.DualRegisters.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                                if (targetRegister != null)
                                {
                                    int dataStoreIndex = e.StartAddress + 1;
                                    if (dataStoreIndex < HoldingRegisters.Count)
                                    {
                                        ushort newValue = HoldingRegisters[dataStoreIndex];
                                        int oldValue = targetRegister.RegisterValue;

                                        if (oldValue != newValue)
                                        {
                                            targetRegister.RegisterValue = newValue;
                                            System.Diagnostics.Debug.WriteLine($"마스터 쓰기 → UI: 장치{currentUnitId} HoldingRegister[{e.StartAddress}]: {oldValue} → {newValue}");
                                        }
                                    }
                                }
                            }
                            else if (e.ModbusDataType == ModbusDataType.InputRegister && device.DualRegisters != null && device.RegisterType == 30001)
                            {
                                var targetRegister = device.DualRegisters.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                                if (targetRegister != null)
                                {
                                    int dataStoreIndex = e.StartAddress + 1;
                                    if (dataStoreIndex < InputRegisters.Count)
                                    {
                                        ushort newValue = InputRegisters[dataStoreIndex];
                                        int oldValue = targetRegister.RegisterValue;

                                        if (oldValue != newValue)
                                        {
                                            targetRegister.RegisterValue = newValue;
                                            System.Diagnostics.Debug.WriteLine($"마스터 쓰기 → UI: 장치{currentUnitId} InputRegister[{e.StartAddress}]: {oldValue} → {newValue}");
                                        }
                                    }
                                }
                            }
                            else if (e.ModbusDataType == ModbusDataType.Coil && device.Coils != null)
                            {
                                var targetRegister = device.Coils.FirstOrDefault(r => r.ModbusAddress == e.StartAddress);
                                if (targetRegister != null)
                                {
                                    int dataStoreIndex = e.StartAddress + 1;
                                    if (dataStoreIndex < CoilDiscretes.Count)
                                    {
                                        bool newValue = CoilDiscretes[dataStoreIndex];
                                        int oldValue = targetRegister.Value;
                                        int expectedValue = newValue ? 1 : 0;

                                        if (oldValue != expectedValue)
                                        {
                                            targetRegister.Value = expectedValue;
                                            System.Diagnostics.Debug.WriteLine($"마스터 쓰기 → UI: 장치{currentUnitId} Coil[{e.StartAddress}]: {oldValue} → {expectedValue}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UI 업데이트 오류: {ex.Message}");
                }
            }));
        }
    }
}