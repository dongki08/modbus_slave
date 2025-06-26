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
        
        private void ShowDeviceData_Click(object sender, RoutedEventArgs e)
        {
            // 버튼 스타일 변경
            DeviceDataButton.Style = (Style)FindResource("ActiveToggleButton");
            LogButton.Style = (Style)FindResource("ToggleButton");
    
            // 헤더 변경
            HeaderIcon.Icon = FontAwesome.Sharp.IconChar.Database;
            HeaderText.Text = "장치 데이터";
    
            // 컨텐츠 표시/숨김
            DeviceTabControl.Visibility = Visibility.Visible;
            LogContainer.Visibility = Visibility.Collapsed;
    
            // 장치 삭제 버튼 표시
            DeleteDeviceButton.Visibility = Visibility.Visible;
        }

// 로그 보기 버튼 클릭
        private void ShowLog_Click(object sender, RoutedEventArgs e)
        {
            // 버튼 스타일 변경
            DeviceDataButton.Style = (Style)FindResource("ToggleButton");
            LogButton.Style = (Style)FindResource("ActiveToggleButton");
    
            // 헤더 변경
            HeaderIcon.Icon = FontAwesome.Sharp.IconChar.FileLines;
            HeaderText.Text = "로그";
    
            // 컨텐츠 표시/숨김
            DeviceTabControl.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Visible;
    
            // 장치 삭제 버튼 숨김
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
                var card = CreateUnifiedRegisterCard(title, device.DualRegisters, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            return mainGrid;
        }

        // 통합 레지스터 카드 생성 (모든 형태 동시 표시)
        private UIElement CreateUnifiedRegisterCard(string title, ObservableCollection<DualRegisterModel> data, bool fillHeight = false)
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

            Border contentContainer = new Border();
            Grid.SetRow(contentContainer, 1);

            ScrollViewer scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            StackPanel registerStack = new StackPanel { Orientation = Orientation.Vertical };

            foreach (var dualRegister in data)
            {
                var panel = CreateUnifiedRegisterPanel(dualRegister);
                registerStack.Children.Add(panel);
            }

            scrollViewer.Content = registerStack;
            contentContainer.Child = scrollViewer;
            cardContent.Children.Add(contentContainer);
            cardBorder.Child = cardContent;

            return cardBorder;
        }

        // 통합 레지스터 패널 생성 (모든 입력 형태를 한 화면에)
private FrameworkElement CreateUnifiedRegisterPanel(DualRegisterModel dualRegister)
{
    Border border = new Border();
    border.Background = new SolidColorBrush(Color.FromRgb(250, 251, 252));
    border.BorderBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230));
    border.BorderThickness = new Thickness(1);
    border.CornerRadius = new CornerRadius(4);
    border.Margin = new Thickness(0, 4, 0, 4);
    border.Padding = new Thickness(12);

    Grid mainGrid = new Grid();
    mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 레지스터 정보
    mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 모든 입력 및 비트 편집 (한 줄)

    // 레지스터 정보 헤더
    TextBlock addressLabel = new TextBlock();
    addressLabel.Text = $"Register {dualRegister.DisplayAddress} (Address: {dualRegister.ModbusAddress}) - Value: {dualRegister.RegisterValue}";
    addressLabel.FontWeight = FontWeights.SemiBold;
    addressLabel.FontSize = 13;
    addressLabel.Foreground = new SolidColorBrush(Color.FromRgb(44, 44, 44));
    addressLabel.Margin = new Thickness(0, 0, 0, 12);
    Grid.SetRow(addressLabel, 0);
    mainGrid.Children.Add(addressLabel);

    // 모든 입력과 비트 편집을 한 줄로 배치
    StackPanel inputAndBitRow = new StackPanel();
    inputAndBitRow.Orientation = Orientation.Horizontal;
    inputAndBitRow.Margin = new Thickness(0, 0, 0, 8);
    Grid.SetRow(inputAndBitRow, 1);

    // 10진수 입력
    StackPanel decimalPanel = new StackPanel();
    decimalPanel.Orientation = Orientation.Horizontal;
    decimalPanel.Margin = new Thickness(0, 0, 10, 0);

    TextBlock decimalLabel = new TextBlock();
    decimalLabel.Text = "10진수: ";
    decimalLabel.VerticalAlignment = VerticalAlignment.Center;
    decimalLabel.FontWeight = FontWeights.Medium;
    decimalLabel.Margin = new Thickness(0, 0, 5, 0);
    decimalPanel.Children.Add(decimalLabel);

    TextBox decimalTextBox = new TextBox();
    decimalTextBox.Width = 50;
    decimalTextBox.Height = 26;
    decimalTextBox.Text = dualRegister.RegisterValue.ToString();
    decimalTextBox.VerticalAlignment = VerticalAlignment.Center;
    decimalTextBox.Tag = "DecimalInput";
    decimalTextBox.TabIndex = 1;
    decimalPanel.Children.Add(decimalTextBox);

    inputAndBitRow.Children.Add(decimalPanel);

    // 16진수 입력
    StackPanel hexPanel = new StackPanel();
    hexPanel.Orientation = Orientation.Horizontal;
    hexPanel.Margin = new Thickness(0, 0, 10, 0);

    TextBlock hexLabel = new TextBlock();
    hexLabel.Text = "16진수: ";
    hexLabel.VerticalAlignment = VerticalAlignment.Center;
    hexLabel.FontWeight = FontWeights.Medium;
    hexLabel.Margin = new Thickness(0, 0, 5, 0);
    hexPanel.Children.Add(hexLabel);

    TextBox hexTextBox = new TextBox();
    hexTextBox.Width = 50;
    hexTextBox.Height = 26;
    hexTextBox.Text = $"0x{dualRegister.RegisterValue:X4}";
    hexTextBox.VerticalAlignment = VerticalAlignment.Center;
    hexTextBox.Tag = "HexInput";
    hexTextBox.TabIndex = 2;
    hexPanel.Children.Add(hexTextBox);

    inputAndBitRow.Children.Add(hexPanel);

    // 문자열 입력
    StackPanel stringPanel = new StackPanel();
    stringPanel.Orientation = Orientation.Horizontal;
    stringPanel.Margin = new Thickness(0, 0, 20, 0);

    TextBlock stringLabel = new TextBlock();
    stringLabel.Text = "문자열: ";
    stringLabel.VerticalAlignment = VerticalAlignment.Center;
    stringLabel.FontWeight = FontWeights.Medium;
    stringLabel.Margin = new Thickness(0, 0, 5, 0);
    stringPanel.Children.Add(stringLabel);

    TextBox stringTextBox = new TextBox();
    stringTextBox.Width = 35;
    stringTextBox.Height = 26;
    stringTextBox.MaxLength = 2;
    stringTextBox.Text = ExtractStringFromRegister(dualRegister.RegisterValue);
    stringTextBox.VerticalAlignment = VerticalAlignment.Center;
    stringTextBox.Tag = "StringInput";
    stringTextBox.TabIndex = 3;
    stringPanel.Children.Add(stringTextBox);

    inputAndBitRow.Children.Add(stringPanel);

    // 2진수 표시 (읽기전용)
    StackPanel binaryPanel = new StackPanel();
    binaryPanel.Orientation = Orientation.Horizontal;
    binaryPanel.Margin = new Thickness(0, 0, 20, 0);

    TextBlock binaryLabel = new TextBlock();
    binaryLabel.Text = "2진수: ";
    binaryLabel.VerticalAlignment = VerticalAlignment.Center;
    binaryLabel.FontWeight = FontWeights.Medium;
    binaryLabel.Margin = new Thickness(0, 0, 5, 0);
    binaryPanel.Children.Add(binaryLabel);

    TextBox binaryDisplayTextBox = new TextBox();
    binaryDisplayTextBox.Text = Convert.ToString(dualRegister.RegisterValue & 0xFFFF, 2).PadLeft(16, '0');
    binaryDisplayTextBox.IsReadOnly = true;
    binaryDisplayTextBox.BorderThickness = new Thickness(1);
    binaryDisplayTextBox.Background = new SolidColorBrush(Color.FromRgb(248, 249, 250));
    binaryDisplayTextBox.FontFamily = new FontFamily("Consolas");
    binaryDisplayTextBox.FontSize = 15;
    binaryDisplayTextBox.Width = 150;
    binaryDisplayTextBox.Height = 26;
    binaryDisplayTextBox.VerticalAlignment = VerticalAlignment.Center;
    binaryDisplayTextBox.Cursor = System.Windows.Input.Cursors.IBeam;
    binaryDisplayTextBox.Tag = "BinaryDisplay";
    binaryPanel.Children.Add(binaryDisplayTextBox);

    inputAndBitRow.Children.Add(binaryPanel);

    // 비트 편집 영역 (오른쪽에 배치)
    StackPanel bitSection = new StackPanel();
    bitSection.Orientation = Orientation.Horizontal;
    bitSection.VerticalAlignment = VerticalAlignment.Center;

    TextBlock bitLabel = new TextBlock();
    bitLabel.Text = "비트 편집: ";
    bitLabel.VerticalAlignment = VerticalAlignment.Center;
    bitLabel.FontWeight = FontWeights.Medium;
    bitLabel.Margin = new Thickness(0, 0, 5, 0);
    bitSection.Children.Add(bitLabel);

    // 비트 편집 그리드 - 원본과 동일한 방식으로 생성
    Grid bitGrid = CreateBitEditGrid(dualRegister);
    bitSection.Children.Add(bitGrid);
    
    inputAndBitRow.Children.Add(bitSection);
    mainGrid.Children.Add(inputAndBitRow);

    // 업데이트 플래그
    bool isInternalUpdate = false;

    // 10진수 입력 이벤트
    Action processDecimalInput = () =>
    {
        if (isInternalUpdate) return;
        
        string inputText = decimalTextBox.Text.Trim();
        if (int.TryParse(inputText, out int value))
        {
            value = Math.Max(0, Math.Min(65535, value));
            if (value != dualRegister.RegisterValue)
            {
                isInternalUpdate = true;
                dualRegister.RegisterValue = value;
                
                // 범위 제한으로 값이 변경된 경우 텍스트박스도 업데이트
                if (decimalTextBox.Text != value.ToString())
                {
                    decimalTextBox.Text = value.ToString();
                }
                
                UpdateAllDisplays(dualRegister, hexTextBox, stringTextBox, binaryDisplayTextBox, bitGrid, addressLabel);
                UpdateCurrentDeviceDataStore();
                isInternalUpdate = false;
            }
        }
        else
        {
            decimalTextBox.Text = dualRegister.RegisterValue.ToString();
        }
    };

    // 16진수 입력 이벤트
    Action processHexInput = () =>
    {
        if (isInternalUpdate) return;
        
        string inputText = hexTextBox.Text.Trim().Replace("0x", "").Replace("0X", "");
        if (int.TryParse(inputText, System.Globalization.NumberStyles.HexNumber, null, out int value))
        {
            value = Math.Max(0, Math.Min(65535, value));
            if (value != dualRegister.RegisterValue)
            {
                isInternalUpdate = true;
                dualRegister.RegisterValue = value;
                UpdateAllDisplays(dualRegister, decimalTextBox, stringTextBox, binaryDisplayTextBox, bitGrid, addressLabel);
                UpdateCurrentDeviceDataStore();
                isInternalUpdate = false;
            }
        }
        else
        {
            hexTextBox.Text = $"0x{dualRegister.RegisterValue:X4}";
        }
    };

    // 문자열 입력 이벤트
    Action processStringInput = () =>
    {
        if (isInternalUpdate) return;
        
        int value = ConvertStringToRegisterValue(stringTextBox.Text);
        if (value != dualRegister.RegisterValue)
        {
            isInternalUpdate = true;
            dualRegister.RegisterValue = value;
            UpdateAllDisplays(dualRegister, decimalTextBox, hexTextBox, binaryDisplayTextBox, bitGrid, addressLabel);
            UpdateCurrentDeviceDataStore();
            isInternalUpdate = false;
        }
    };

    // 이벤트 핸들러 등록
    decimalTextBox.KeyDown += (s, e) => 
    { 
        if (e.Key == System.Windows.Input.Key.Enter) 
        { 
            processDecimalInput(); 
            e.Handled = true; // 포커스 이동 완전 차단
            // 포커스를 현재 텍스트박스에 유지
            Dispatcher.BeginInvoke(new Action(() => 
            {
                decimalTextBox.Focus();
                decimalTextBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        } 
    };
    decimalTextBox.LostFocus += (s, e) => processDecimalInput();

    hexTextBox.KeyDown += (s, e) => 
    { 
        if (e.Key == System.Windows.Input.Key.Enter) 
        { 
            processHexInput(); 
            e.Handled = true; // 포커스 이동 완전 차단
            // 포커스를 현재 텍스트박스에 유지
            Dispatcher.BeginInvoke(new Action(() => 
            {
                hexTextBox.Focus();
                hexTextBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        } 
    };
    hexTextBox.LostFocus += (s, e) => processHexInput();

    stringTextBox.KeyDown += (s, e) => 
    { 
        if (e.Key == System.Windows.Input.Key.Enter) 
        { 
            processStringInput(); 
            e.Handled = true; // 포커스 이동 완전 차단
            // 포커스를 현재 텍스트박스에 유지
            Dispatcher.BeginInvoke(new Action(() => 
            {
                stringTextBox.Focus();
                stringTextBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        } 
    };
    stringTextBox.LostFocus += (s, e) => processStringInput();

    // 외부에서 레지스터 값이 변경될 때 UI 업데이트
    dualRegister.PropertyChanged += (sender, e) =>
    {
        if (e.PropertyName == nameof(DualRegisterModel.RegisterValue) && !isInternalUpdate)
        {
            isInternalUpdate = true;
            
            if (!decimalTextBox.IsFocused)
                decimalTextBox.Text = dualRegister.RegisterValue.ToString();
            if (!hexTextBox.IsFocused)
                hexTextBox.Text = $"0x{dualRegister.RegisterValue:X4}";
            if (!stringTextBox.IsFocused)
                stringTextBox.Text = ExtractStringFromRegister(dualRegister.RegisterValue);
                
            binaryDisplayTextBox.Text = Convert.ToString(dualRegister.RegisterValue & 0xFFFF, 2).PadLeft(16, '0');
            addressLabel.Text = $"Register {dualRegister.DisplayAddress} (Address: {dualRegister.ModbusAddress}) - Value: {dualRegister.RegisterValue}";
            
            UpdateBitGridFromRegister(dualRegister, bitGrid);
            
            isInternalUpdate = false;
        }
    };

    // isInternalUpdate 플래그를 비트 그리드 핸들러에서도 사용할 수 있도록 연결
    bitGrid.Tag = new Func<bool>(() => isInternalUpdate);
    
    border.Child = mainGrid;
    return border;
}

        // 모든 표시 업데이트
        private void UpdateAllDisplays(DualRegisterModel dualRegister, params object[] controls)
        {
            foreach (var control in controls)
            {
                if (control is TextBox textBox)
                {
                    string tag = textBox.Tag?.ToString();
                    if (!textBox.IsFocused)
                    {
                        switch (tag)
                        {
                            case "DecimalInput":
                                textBox.Text = dualRegister.RegisterValue.ToString();
                                break;
                            case "HexInput":
                                textBox.Text = $"0x{dualRegister.RegisterValue:X4}";
                                break;
                            case "StringInput":
                                textBox.Text = ExtractStringFromRegister(dualRegister.RegisterValue);
                                break;
                            case "BinaryDisplay":
                                textBox.Text = Convert.ToString(dualRegister.RegisterValue & 0xFFFF, 2).PadLeft(16, '0');
                                break;
                        }
                    }
                }
                else if (control is Grid bitGrid)
                {
                    UpdateBitGridFromRegister(dualRegister, bitGrid);
                }
                else if (control is TextBlock addressLabel)
                {
                    addressLabel.Text = $"Register {dualRegister.DisplayAddress} (Address: {dualRegister.ModbusAddress}) - Value: {dualRegister.RegisterValue}";
                }
            }
        }

        // 비트 편집 그리드 생성
        private Grid CreateBitEditGrid(DualRegisterModel dualRegister)
        {
            Grid bitGrid = new Grid();
            bitGrid.HorizontalAlignment = HorizontalAlignment.Left;

            // 행 정의
            bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 비트 번호
            bitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 비트 값

            // 열 정의 (16개 비트)
            for (int i = 0; i < 16; i++)
            {
                bitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            }

            // 비트 번호 라벨 (15부터 0까지)
            for (int bit = 15; bit >= 0; bit--)
            {
                TextBlock bitNumberLabel = new TextBlock();
                bitNumberLabel.Text = bit.ToString();
                bitNumberLabel.FontSize = 8;
                bitNumberLabel.FontWeight = FontWeights.Bold;
                bitNumberLabel.Foreground = Brushes.DarkBlue;
                bitNumberLabel.TextAlignment = TextAlignment.Center;
                bitNumberLabel.HorizontalAlignment = HorizontalAlignment.Center;
                bitNumberLabel.Margin = new Thickness(1, -10, 1, 0);
                
                //Grid.SetRow(bitNumberLabel, 0);
                
                Grid.SetColumn(bitNumberLabel, 15 - bit);
                bitGrid.Children.Add(bitNumberLabel);
            }

            // 비트 값 텍스트박스 (15부터 0까지)
            for (int bit = 15; bit >= 0; bit--)
            {
                TextBox bitTextBox = new TextBox();
                bitTextBox.Width = 26;
                bitTextBox.Height = 26;
                bitTextBox.FontSize = 11;
                bitTextBox.FontWeight = FontWeights.Bold;
                bitTextBox.TextAlignment = TextAlignment.Center;
                bitTextBox.VerticalContentAlignment = VerticalAlignment.Center;
                bitTextBox.MaxLength = 1;
                bitTextBox.Tag = bit;
                bitTextBox.TabIndex = 100 + (15 - bit); // 비트는 훨씬 큰 TabIndex로 설정하여 자동 포커스 방지
                bitTextBox.IsTabStop = false; // Tab으로 이동 불가능하게 설정

                int bitValue = (dualRegister.RegisterValue >> bit) & 1;
                bitTextBox.Text = bitValue.ToString();
                Grid.SetRow(bitTextBox, 0);
                UpdateBitTextBoxAppearance(bitTextBox, bitValue);

                // 비트 변경 이벤트
                bitTextBox.TextChanged += (sender, e) =>
                {
                    // *** 수정된 부분 ***
                    // isInternalUpdate 상태를 가져오기 위해 상위 컨텍스트를 탐색
                    var isInternalUpdateFunc = bitGrid.Tag as Func<bool>;
                    if (isInternalUpdateFunc != null && isInternalUpdateFunc())
                    {
                        return; // 내부 업데이트 중에는 이벤트를 무시하여 연쇄 반응 방지
                    }

                    var tb = sender as TextBox;
                    if (tb.Text == "0" || tb.Text == "1")
                    {
                        int newBitValue = int.Parse(tb.Text);
                        UpdateBitTextBoxAppearance(tb, newBitValue);
                        UpdateRegisterFromBitGrid(dualRegister, bitGrid);
                        UpdateCurrentDeviceDataStore();
                    }
                    else if (!string.IsNullOrEmpty(tb.Text))
                    {
                        // 잘못된 입력은 원래값으로 되돌리기
                        int originalBitValue = (dualRegister.RegisterValue >> (int)tb.Tag) & 1;
                        tb.Text = originalBitValue.ToString();
                        UpdateBitTextBoxAppearance(tb, originalBitValue);
                    }
                };

                // 포커스 시 전체 선택
                bitTextBox.GotFocus += (sender, e) =>
                {
                    var tb = sender as TextBox;
                    tb.SelectAll();
                };

                // 키 입력 처리
                bitTextBox.KeyDown += (sender, e) =>
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
                    else if (e.Key == System.Windows.Input.Key.Enter)
                    {
                        // Enter 키를 눌렀을 때 다음 비트로 이동 (왼쪽으로)
                        int currentBit = (int)tb.Tag;
                        if (currentBit > 0) // 0번 비트가 아닌 경우
                        {
                            // 다음 비트 (현재 비트 - 1) 찾기
                            var nextBitTextBox = bitGrid.Children.OfType<TextBox>()
                                .FirstOrDefault(t => (int)t.Tag == currentBit - 1);
                            if (nextBitTextBox != null)
                            {
                                nextBitTextBox.Focus();
                                nextBitTextBox.SelectAll();
                            }
                        }
                        else
                        {
                            // 0번 비트에서 Enter를 누르면 포커스를 벗어남 (10진수 입력으로)
                            var decimalTextBox = FindDecimalTextBox(bitGrid);
                            if (decimalTextBox != null)
                            {
                                decimalTextBox.Focus();
                                decimalTextBox.SelectAll();
                            }
                        }
                        e.Handled = true;
                    }
                    else if (e.Key == System.Windows.Input.Key.Left)
                    {
                        // 왼쪽 화살표 키로 다음 비트로 이동
                        int currentBit = (int)tb.Tag;
                        if (currentBit > 0)
                        {
                            var nextBitTextBox = bitGrid.Children.OfType<TextBox>()
                                .FirstOrDefault(t => (int)t.Tag == currentBit - 1);
                            if (nextBitTextBox != null)
                            {
                                nextBitTextBox.Focus();
                                nextBitTextBox.SelectAll();
                            }
                        }
                        e.Handled = true;
                    }
                    else if (e.Key == System.Windows.Input.Key.Right)
                    {
                        // 오른쪽 화살표 키로 이전 비트로 이동
                        int currentBit = (int)tb.Tag;
                        if (currentBit < 15)
                        {
                            var prevBitTextBox = bitGrid.Children.OfType<TextBox>()
                                .FirstOrDefault(t => (int)t.Tag == currentBit + 1);
                            if (prevBitTextBox != null)
                            {
                                prevBitTextBox.Focus();
                                prevBitTextBox.SelectAll();
                            }
                        }
                        e.Handled = true;
                    }
                    else if (e.Key == System.Windows.Input.Key.Tab)
                    {
                        // Tab 키는 기본 동작 허용 (다른 컨트롤로 이동)
                    }
                    else if (e.Key == System.Windows.Input.Key.Delete || 
                             e.Key == System.Windows.Input.Key.Back)
                    {
                        // Delete/Backspace는 0으로 설정
                        tb.Text = "0";
                        e.Handled = true;
                    }
                    else if (e.Key == System.Windows.Input.Key.Escape)
                    {
                        // ESC 키로 비트 편집 영역에서 벗어남
                        var decimalTextBox = FindDecimalTextBox(bitGrid);
                        if (decimalTextBox != null)
                        {
                            decimalTextBox.Focus();
                        }
                        e.Handled = true;
                    }
                    else
                    {
                        // 다른 키는 차단
                        e.Handled = true;
                    }
                };

                // 더블클릭으로 토글
                bitTextBox.MouseDoubleClick += (sender, e) =>
                {
                    var tb = sender as TextBox;
                    tb.Text = (tb.Text == "0") ? "1" : "0";
                };

                Grid.SetRow(bitTextBox, 1);
                Grid.SetColumn(bitTextBox, 15 - bit);
                bitGrid.Children.Add(bitTextBox);
            }

            return bitGrid;
        }

        // 비트 그리드에서 레지스터 값 업데이트
        private void UpdateRegisterFromBitGrid(DualRegisterModel dualRegister, Grid bitGrid)
        {
            int newValue = 0;
            
            foreach (TextBox textBox in bitGrid.Children.OfType<TextBox>())
            {
                if (int.TryParse(textBox.Text, out int bitValue) && (bitValue == 0 || bitValue == 1))
                {
                    int bitPosition = (int)textBox.Tag;
                    if (bitValue == 1)
                    {
                        newValue |= (1 << bitPosition);
                    }
                }
            }
            
            // 값이 실제로 변경된 경우에만 업데이트
            if (newValue != dualRegister.RegisterValue)
            {
                dualRegister.RegisterValue = newValue;
            }
        }

        // 레지스터에서 비트 그리드 업데이트
        private void UpdateBitGridFromRegister(DualRegisterModel dualRegister, Grid bitGrid)
        {
            foreach (TextBox textBox in bitGrid.Children.OfType<TextBox>())
            {
                if (textBox.IsFocused) continue; // 포커스된 텍스트박스는 업데이트하지 않음
                
                int bitPosition = (int)textBox.Tag;
                int bitValue = (dualRegister.RegisterValue >> bitPosition) & 1;
                
                if (textBox.Text != bitValue.ToString())
                {
                    textBox.Text = bitValue.ToString();
                    UpdateBitTextBoxAppearance(textBox, bitValue);
                }
            }
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

        // 레지스터 값에서 문자열 추출
        private string ExtractStringFromRegister(int registerValue)
        {
            StringBuilder sb = new StringBuilder();
            
            // 상위 바이트 (첫 번째 문자)
            char char1 = (char)((registerValue >> 8) & 0xFF);
            if (char1 >= 32 && char1 <= 126)
            {
                sb.Append(char1);
            }
            
            // 하위 바이트 (두 번째 문자)
            char char2 = (char)(registerValue & 0xFF);
            if (char2 >= 32 && char2 <= 126)
            {
                sb.Append(char2);
            }
            
            return sb.ToString();
        }

        // 문자열을 레지스터 값으로 변환
        private int ConvertStringToRegisterValue(string input)
        {
            if (string.IsNullOrEmpty(input))
                return 0;
            
            int value = 0;
            
            // 첫 번째 문자 (상위 바이트)
            if (input.Length >= 1)
            {
                value |= ((int)input[0] << 8);
            }
            
            // 두 번째 문자 (하위 바이트)
            if (input.Length >= 2)
            {
                value |= (int)input[1];
            }
            
            return value & 0xFFFF;
        }

        // 10진수 텍스트박스 찾기 헬퍼 메소드
        private TextBox FindDecimalTextBox(Grid bitGrid)
        {
            // 비트 그리드의 부모들을 타고 올라가서 10진수 텍스트박스 찾기
            var parent = bitGrid.Parent;
            while (parent != null)
            {
                if (parent is FrameworkElement element)
                {
                    var decimalTextBox = FindChildTextBox(element, "DecimalInput");
                    if (decimalTextBox != null)
                        return decimalTextBox;
                    parent = element.Parent;
                }
                else
                {
                    break;
                }
            }
            return null;
        }

        // 자식 요소에서 태그로 텍스트박스 찾기
        private TextBox FindChildTextBox(DependencyObject parent, string tag)
        {
            if (parent == null) return null;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                
                if (child is TextBox textBox && textBox.Tag?.ToString() == tag)
                {
                    return textBox;
                }

                var result = FindChildTextBox(child, tag);
                if (result != null)
                    return result;
            }
            return null;
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