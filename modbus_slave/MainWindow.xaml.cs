using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Modbus.Device;
using Microsoft.Win32;

namespace modbus_slave
{
    public partial class MainWindow : Window
    {
        #region Data Models

        public class SlaveUnitData : INotifyPropertyChanged
        {
            private byte _unitId;
            private string _header;

            public byte UnitId
            {
                get => _unitId;
                set
                {
                    _unitId = value;
                    OnPropertyChanged(nameof(UnitId));
                }
            }

            public string Header
            {
                get => _header;
                set
                {
                    _header = value;
                    OnPropertyChanged(nameof(Header));
                }
            }

            public Dictionary<int, bool> Coils { get; set; } = new Dictionary<int, bool>();
            public Dictionary<int, bool> DiscreteInputs { get; set; } = new Dictionary<int, bool>();
            public Dictionary<int, ushort> HoldingRegisters { get; set; } = new Dictionary<int, ushort>();
            public Dictionary<int, ushort> InputRegisters { get; set; } = new Dictionary<int, ushort>();

            public DataTable CoilsTable { get; set; }
            public DataTable DiscreteTable { get; set; }
            public DataTable HoldingTable { get; set; }
            public DataTable InputTable { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        

        public class ClientInfo : INotifyPropertyChanged
        {
            private string _endPoint;
            private string _connectedTime;
            private bool _isConnected;

            public string EndPoint
            {
                get => _endPoint;
                set
                {
                    _endPoint = value;
                    OnPropertyChanged(nameof(EndPoint));
                }
            }

            public string ConnectedTime
            {
                get => _connectedTime;
                set
                {
                    _connectedTime = value;
                    OnPropertyChanged(nameof(ConnectedTime));
                }
            }

            public bool IsConnected
            {
                get => _isConnected;
                set
                {
                    _isConnected = value;
                    OnPropertyChanged(nameof(IsConnected));
                }
            }

            public TcpClient TcpClient { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public enum DataType
        {
            Coils,
            DiscreteInputs,
            HoldingRegisters,
            InputRegisters
        }

        #endregion

        #region Private Fields

        private readonly ObservableCollection<SlaveUnitData> _units = new ObservableCollection<SlaveUnitData>();
        private readonly ObservableCollection<ClientInfo> _clients = new ObservableCollection<ClientInfo>();
        private SlaveUnitData _currentUnit;
        private DataType _currentDataType = DataType.Coils;

        private TcpListener _tcpListener;
        private CancellationTokenSource _serverCts;
        private bool _isServerRunning;

        private volatile bool _isUpdatingFormats = false;
        private bool _isLogCollapsed = false;
        // 통계
        private long _requestCount;
        private long _errorCount;

        // 시뮬레이션
        private Timer _simulationTimer;
        private readonly Random _random = new Random();

        // Custom DataStore
        private CustomSlaveDataStore _dataStore;

        // UI Resources
        private Brush _successBrush;
        private Brush _errorBrush;
        private Brush _warningBrush;

        #endregion

        #region Constructor & Initialization

        public MainWindow()
        {
            InitializeComponent();
            InitializeResources();
            InitializeUnits();
            InitializeDataStore();
            SetupEventHandlers();
    
            // 초기화 완료 후 기본 상태 설정
            SetDefaultDataType();
            UpdateUI();
        }
        
        private void SetDefaultDataType()
        {
            // 프로그래밍 방식으로 기본 데이터 타입 설정
            btnCoils.IsChecked = true;
            _currentDataType = DataType.Coils;
            UpdateDataGrid();
        }

        private void InitializeResources()
        {
            _successBrush = (Brush)FindResource("SuccessBrush");
            _errorBrush = (Brush)FindResource("ErrorBrush");
            _warningBrush = (Brush)FindResource("WarningBrush");
        }

        private void InitializeUnits()
        {
            // 기본 Unit 1 추가
            AddNewUnit(1);
        }

        private void InitializeDataStore()
        {
            _dataStore = new CustomSlaveDataStore(_units, this.Dispatcher, this); // this 추가
            _dataStore.RequestReceived += OnModbusRequestReceived;
        }

        private void SetupEventHandlers()
        {
            lstClients.ItemsSource = _clients;
            dgCoilsData.CellEditEnding += DgData_CellEditEnding;
            dgRegistersData.CellEditEnding += DgData_CellEditEnding;
        }

        private DataTable CreateCoilTable()
        {
            var table = new DataTable();
            table.Columns.Add("Address", typeof(string));
            table.Columns.Add("Value", typeof(bool));
            table.ColumnChanged += Table_ColumnChanged;
            return table;
        }

        private DataTable CreateRegisterTable()
        {
            var table = new DataTable();
            table.Columns.Add("Address", typeof(string));
            table.Columns.Add("Value", typeof(ushort));
            table.Columns.Add("ValueHex", typeof(string));
            table.Columns.Add("ValueString", typeof(string));

            for (int i = 0; i < 16; i++)
            {
                table.Columns.Add($"Bit{i}", typeof(bool));
            }

            table.ColumnChanged += Table_ColumnChanged;
            return table;
        }

        #endregion

        #region Custom Window Chrome

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) AdjustWindowSize();
            else DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => AdjustWindowSize();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void AdjustWindowSize() =>
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;

        #endregion

        #region Unit Management

        private void btnAddUnit_Click(object sender, RoutedEventArgs e)
        {
            byte unitId;

            if (!string.IsNullOrEmpty(txtNewUnitId.Text) && byte.TryParse(txtNewUnitId.Text, out unitId))
            {
                if (_units.Any(u => u.UnitId == unitId))
                {
                    Log($"[!] Unit ID {unitId} already exists.");
                    return;
                }
            }
            else
            {
                unitId = GetNextAvailableUnitId();
            }

            AddNewUnit(unitId);
            txtNewUnitId.Clear();
        }

        private void btnRemoveUnit_Click(object sender, RoutedEventArgs e)
        {
            if (_units.Count <= 1)
            {
                Log("[!] At least one unit must remain.");
                return;
            }

            if (_currentUnit != null)
            {
                RemoveUnit(_currentUnit);
            }
        }

        private void AddNewUnit(byte unitId)
        {
            var unit = new SlaveUnitData
            {
                Header = $"Unit {unitId}",
                UnitId = unitId,
                CoilsTable = CreateCoilTable(),
                DiscreteTable = CreateCoilTable(),
                HoldingTable = CreateRegisterTable(),
                InputTable = CreateRegisterTable()
            };

            _units.Add(unit);
            CreateUnitTab(unit);
            SelectUnit(unit);
            Log($"[+] Added Unit {unitId}");
        }

        private void RemoveUnit(SlaveUnitData unit)
        {
            var tabToRemove = spUnitTabs.Children.OfType<ToggleButton>()
                .FirstOrDefault(btn => btn.Tag == unit);
            if (tabToRemove != null)
            {
                spUnitTabs.Children.Remove(tabToRemove);
            }

            _units.Remove(unit);

            if (_units.Count > 0)
            {
                var firstUnit = _units.First();
                SelectUnit(firstUnit);
                var firstTab = spUnitTabs.Children.OfType<ToggleButton>().FirstOrDefault();
                if (firstTab != null) firstTab.IsChecked = true;
            }

            Log($"[-] Removed Unit {unit.UnitId}");
        }

        private void CreateUnitTab(SlaveUnitData unit)
        {
            var toggleButton = new ToggleButton
            {
                Content = $"Unit {unit.UnitId}",
                Style = (Style)FindResource("SegmentedToggleButton"),
                Tag = unit,
                Margin = new Thickness(0, 0, 4, 0),
                MinWidth = 80,
                Height = 32
            };

            toggleButton.Checked += UnitTab_Checked;
            spUnitTabs.Children.Add(toggleButton);
        }

        private void UnitTab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggleButton && toggleButton.Tag is SlaveUnitData unit)
            {
                foreach (ToggleButton btn in spUnitTabs.Children.OfType<ToggleButton>())
                {
                    if (btn != toggleButton) btn.IsChecked = false;
                }

                SelectUnit(unit);
            }
        }

        private void SelectUnit(SlaveUnitData unit)
        {
            _currentUnit = unit;
            UpdateDataGrid();
        }

        private byte GetNextAvailableUnitId()
        {
            var usedIds = _units.Select(u => u.UnitId).ToHashSet();
            for (byte i = 1; i <= 255; i++)
            {
                if (!usedIds.Contains(i)) return i;
            }

            return 1;
        }

        #endregion

        #region Data Type Toggle Events

        private void btnCoils_Checked(object sender, RoutedEventArgs e)
        {
            if (btnDiscrete == null || btnHolding == null || btnInput == null) return;
    
            btnDiscrete.IsChecked = false;
            btnHolding.IsChecked = false;
            btnInput.IsChecked = false;
            _currentDataType = DataType.Coils;
            UpdateDataGrid();
        }

        private void btnDiscrete_Checked(object sender, RoutedEventArgs e)
        {
            if (btnCoils == null || btnHolding == null || btnInput == null) return;
    
            btnCoils.IsChecked = false;
            btnHolding.IsChecked = false;
            btnInput.IsChecked = false;
            _currentDataType = DataType.DiscreteInputs;
            UpdateDataGrid();
        }

        private void btnHolding_Checked(object sender, RoutedEventArgs e)
        {
            if (btnCoils == null || btnDiscrete == null || btnInput == null) return;
    
            btnCoils.IsChecked = false;
            btnDiscrete.IsChecked = false;
            btnInput.IsChecked = false;
            _currentDataType = DataType.HoldingRegisters;
            UpdateDataGrid();
        }

        private void btnInput_Checked(object sender, RoutedEventArgs e)
        {
            if (btnCoils == null || btnDiscrete == null || btnHolding == null) return;
    
            btnCoils.IsChecked = false;
            btnDiscrete.IsChecked = false;
            btnHolding.IsChecked = false;
            _currentDataType = DataType.InputRegisters;
            UpdateDataGrid();
        }

        #endregion

        #region Data Grid Management

        private void UpdateDataGrid()
        {
            if (_currentUnit == null || dgCoilsData == null || dgRegistersData == null || lblDataType == null) return;

            DataTable currentTable = null;
            string dataTypeName = "";

            switch (_currentDataType)
            {
                case DataType.Coils:
                    currentTable = _currentUnit.CoilsTable;
                    dataTypeName = "📊 Coils Data";
                    dgCoilsData.Visibility = Visibility.Visible;
                    dgRegistersData.Visibility = Visibility.Collapsed;
                    dgCoilsData.ItemsSource = currentTable?.DefaultView;
                    break;
                case DataType.DiscreteInputs:
                    currentTable = _currentUnit.DiscreteTable;
                    dataTypeName = "📊 Discrete Inputs Data";
                    dgCoilsData.Visibility = Visibility.Visible;
                    dgRegistersData.Visibility = Visibility.Collapsed;
                    dgCoilsData.ItemsSource = currentTable?.DefaultView;
                    break;
                case DataType.HoldingRegisters:
                    currentTable = _currentUnit.HoldingTable;
                    dataTypeName = "📊 Holding Registers Data";
                    dgCoilsData.Visibility = Visibility.Collapsed;
                    dgRegistersData.Visibility = Visibility.Visible;
                    dgRegistersData.ItemsSource = currentTable?.DefaultView;
                    break;
                case DataType.InputRegisters:
                    currentTable = _currentUnit.InputTable;
                    dataTypeName = "📊 Input Registers Data";
                    dgCoilsData.Visibility = Visibility.Collapsed;
                    dgRegistersData.Visibility = Visibility.Visible;
                    dgRegistersData.ItemsSource = currentTable?.DefaultView;
                    break;
            }

            lblDataType.Text = dataTypeName;
        }

        private void btnAddRange_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddRangeDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                AddAddressRange(dialog.StartAddress, dialog.Count);
            }
        }

        private void AddAddressRange(int startAddr, int count)
        {
            if (_currentUnit == null) return;

            DataTable table = GetCurrentTable();
            if (table == null) return;

            for (int i = 0; i < count; i++)
            {
                int addr = startAddr + i;

                // LINQ를 사용하지 않고 직접 확인
                bool addressExists = false;
                foreach (DataRow existingRow in table.Rows)
                {
                    if (existingRow["Address"].ToString() == addr.ToString())
                    {
                        addressExists = true;
                        break;
                    }
                }

                if (addressExists) continue;

                var row = table.NewRow();
                row["Address"] = addr.ToString();

                if (_currentDataType == DataType.Coils || _currentDataType == DataType.DiscreteInputs)
                {
                    row["Value"] = false;
                }
                else
                {
                    row["Value"] = (ushort)0;
                    row["ValueHex"] = "0x0000";
                    row["ValueString"] = "";
                    for (int bit = 0; bit < 16; bit++)
                    {
                        row[$"Bit{bit}"] = false;
                    }

                }

                table.Rows.Add(row);
            }

            SyncTableToDataStore();
            Log($"[+] Added address range {startAddr}-{startAddr + count - 1} to {_currentDataType}");
        }

        private DataTable GetCurrentTable()
        {
            switch (_currentDataType)
            {
                case DataType.Coils: return _currentUnit?.CoilsTable;
                case DataType.DiscreteInputs: return _currentUnit?.DiscreteTable;
                case DataType.HoldingRegisters: return _currentUnit?.HoldingTable;
                case DataType.InputRegisters: return _currentUnit?.InputTable;
                default: return null;
            }
        }

        private void btnClearData_Click(object sender, RoutedEventArgs e)
        {
            var table = GetCurrentTable();
            if (table != null)
            {
                table.Clear();
                SyncTableToDataStore();
                Log($"[*] Cleared {_currentDataType} data for Unit {_currentUnit?.UnitId}");
            }
        }

        #endregion

        #region DataGrid Event Handlers

        private void DgData_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;

            var row = ((DataRowView)e.Row.Item).Row;
            var columnHeader = e.Column.Header.ToString();

            if (!(e.EditingElement is TextBox textBox)) return;
            var newValue = textBox.Text;

            try
            {
                // Register 값 변환 및 업데이트
                if (TryConvertValue(columnHeader, newValue, out ushort convertedValue))
                {
                    _isUpdatingFormats = true;
                    try
                    {
                        var table = row.Table;
                        table.ColumnChanged -= Table_ColumnChanged;
                    
                        // 모든 관련 필드 업데이트
                        row["Value"] = convertedValue;
                        row["ValueHex"] = $"0x{convertedValue:X4}";
                    
                        var (highByte, lowByte) = ((byte)(convertedValue >> 8), (byte)(convertedValue & 0xFF));
                        row["ValueString"] = GetStringRepresentation(highByte, lowByte);
                    
                        // 비트 업데이트
                        for (int i = 0; i < 16; i++)
                        {
                            row[$"Bit{i}"] = (convertedValue & (1 << i)) != 0;
                        }
                    
                        table.ColumnChanged += Table_ColumnChanged;
                    }
                    finally
                    {
                        _isUpdatingFormats = false;
                    }
                }
                else
                {
                    e.Cancel = true;
                    Log($"[!] Invalid value: {newValue} for {columnHeader}");
                }
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                LogError("Value conversion failed", ex);
            }
        }

        private bool TryConvertValue(string columnHeader, string value, out ushort result)
        {
            result = 0;

            switch (columnHeader)
            {
                case "Dec":
                    return ushort.TryParse(value, out result);
                case "Hex":
                    return ushort.TryParse(value.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null,
                        out result);
                case "String":
                    return TryConvertStringToUShort(value, out result);
                default:
                    return false;
            }
        }

        private bool TryConvertStringToUShort(string value, out ushort result)
        {
            result = 0;
            if (value.Length > 2) return false;

            byte highByte = 0, lowByte = 0;
            if (value.Length >= 1) lowByte = (byte)value[0];
            if (value.Length == 2)
            {
                highByte = (byte)value[0];
                lowByte = (byte)value[1];
            }

            result = (ushort)((highByte << 8) | lowByte);
            return true;
        }

        private void UpdateRegisterFormats(DataRow row, ushort value)
        {
            if (_isUpdatingFormats) return;
    
            try
            {
                var table = row.Table;
                table.ColumnChanged -= Table_ColumnChanged;
        
                row["ValueHex"] = $"0x{value:X4}";
        
                var (highByte, lowByte) = ((byte)(value >> 8), (byte)(value & 0xFF));
                row["ValueString"] = GetStringRepresentation(highByte, lowByte);
        
                for (int i = 0; i < 16; i++)
                {
                    row[$"Bit{i}"] = (value & (1 << i)) != 0;
                }
        
                table.ColumnChanged += Table_ColumnChanged;
            }
            catch (Exception ex)
            {
                LogError("Error updating register formats", ex);
            }
        }

        private string GetStringRepresentation(byte highByte, byte lowByte)
        {
            if (highByte == 0)
                return GetPrintableChar(lowByte).Replace(".", "");
            if (lowByte == 0)
                return GetPrintableChar(highByte).Replace(".", "");

            var highChar = GetPrintableChar(highByte).Replace(".", "");
            var lowChar = GetPrintableChar(lowByte).Replace(".", "");
            return highChar + lowChar;
        }

        private string GetPrintableChar(byte b) =>
            (b >= 32 && b <= 126) ? ((char)b).ToString() : ".";

        private void Table_ColumnChanged(object sender, DataColumnChangeEventArgs e)
        {
            try
            {
                if (e.Row == null || _isUpdatingFormats) return;
        
                if (_currentDataType == DataType.HoldingRegisters || _currentDataType == DataType.InputRegisters)
                {
                    if (IsBitColumn(e.Column?.ColumnName))
                    {
                        _isUpdatingFormats = true;
                        try
                        {
                            var table = e.Row.Table;
                            table.ColumnChanged -= Table_ColumnChanged;
                    
                            var value = CalculateValueFromBits(e.Row);
                    
                            // 모든 관련 필드 업데이트
                            e.Row["Value"] = value;
                            e.Row["ValueHex"] = $"0x{value:X4}";
                    
                            var (highByte, lowByte) = ((byte)(value >> 8), (byte)(value & 0xFF));
                            e.Row["ValueString"] = GetStringRepresentation(highByte, lowByte);
                    
                            table.ColumnChanged += Table_ColumnChanged;
                        }
                        finally
                        {
                            _isUpdatingFormats = false;
                        }
                    }
                }
        
                // DataStore 동기화
                Task.Run(async () =>
                {
                    await Task.Delay(100);
                    await Dispatcher.InvokeAsync(SyncTableToDataStore);
                });
            }
            catch (Exception ex)
            {
                LogError("Error in Table_ColumnChanged", ex);
            }
        }

        private bool IsBitColumn(string columnName) =>
            !string.IsNullOrEmpty(columnName) && columnName.StartsWith("Bit");

        private ushort CalculateValueFromBits(DataRow row)
        {
            ushort value = 0;
            for (int i = 0; i < 16; i++)
            {
                if (row[$"Bit{i}"] is bool bitValue && bitValue)
                {
                    value |= (ushort)(1 << i);
                }
            }

            return value;
        }

        private void SyncTableToDataStore()
        {
            if (_currentUnit == null) return;

            try
            {
                switch (_currentDataType)
                {
                    case DataType.Coils:
                        SyncCoilsTable(_currentUnit.CoilsTable, _currentUnit.Coils);
                        break;
                    case DataType.DiscreteInputs:
                        SyncCoilsTable(_currentUnit.DiscreteTable, _currentUnit.DiscreteInputs);
                        break;
                    case DataType.HoldingRegisters:
                        SyncRegistersTable(_currentUnit.HoldingTable, _currentUnit.HoldingRegisters);
                        break;
                    case DataType.InputRegisters:
                        SyncRegistersTable(_currentUnit.InputTable, _currentUnit.InputRegisters);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogError("Error syncing table to datastore", ex);
            }
        }

        private void SyncCoilsTable(DataTable table, Dictionary<int, bool> dictionary)
        {
            if (table == null || dictionary == null) return;

            dictionary.Clear();
            foreach (DataRow row in table.Rows)
            {
                if (int.TryParse(row["Address"].ToString(), out int addr))
                {
                    dictionary[addr] = (bool)row["Value"];
                }
            }
        }

        private void SyncRegistersTable(DataTable table, Dictionary<int, ushort> dictionary)
        {
            if (table == null || dictionary == null) return;

            dictionary.Clear();
            foreach (DataRow row in table.Rows)
            {
                if (int.TryParse(row["Address"].ToString(), out int addr))
                {
                    dictionary[addr] = (ushort)row["Value"];
                }
            }
        }

        #endregion

        #region Server Management

        private async void btnToggleServer_Click(object sender, RoutedEventArgs e)
        {
            if (_isServerRunning)
                await StopServerAsync();
            else
                await StartServerAsync();
        }

        private async Task StartServerAsync()
        {
            try
            {
                if (!ValidateServerSettings()) return;

                var ip = IPAddress.Parse(txtServerIp.Text);
                var port = int.Parse(txtServerPort.Text);

                _tcpListener = new TcpListener(ip, port);
                _tcpListener.Start();

                _serverCts = new CancellationTokenSource();
                _isServerRunning = true;

                Log($"[+] Server started on {ip}:{port}");
                UpdateServerStatus(true, $"Server Running on {ip}:{port}");

                // 클라이언트 연결 대기 루프 시작
                _ = Task.Run(() => AcceptClientsLoop(_serverCts.Token));
            }
            catch (Exception ex)
            {
                _isServerRunning = false;
                LogError("Failed to start server", ex);
                UpdateServerStatus(false, "Server Start Failed");
            }
            finally
            {
                UpdateUI();
            }
        }

        private async Task StopServerAsync()
        {
            try
            {
                _serverCts?.Cancel();
                _tcpListener?.Stop();

                // 모든 클라이언트 연결 해제
                var clientsToRemove = _clients.ToList();
                foreach (var client in clientsToRemove)
                {
                    await DisconnectClient(client);
                }

                _isServerRunning = false;
                Log("[-] Server stopped");
                UpdateServerStatus(false, "Server Stopped");
            }
            catch (Exception ex)
            {
                LogError("Error stopping server", ex);
            }
            finally
            {
                UpdateUI();
            }
        }

        private bool ValidateServerSettings()
        {
            if (!IPAddress.TryParse(txtServerIp.Text, out _))
            {
                Log("[!] Invalid IP address");
                return false;
            }

            if (!int.TryParse(txtServerPort.Text, out var port) || port < 1 || port > 65535)
            {
                Log("[!] Invalid port number");
                return false;
            }

            return true;
        }

        private async Task AcceptClientsLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _tcpListener.AcceptTcpClientAsync();
                    var clientInfo = new ClientInfo
                    {
                        TcpClient = tcpClient,
                        EndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "Unknown",
                        ConnectedTime = DateTime.Now.ToString("HH:mm:ss"),
                        IsConnected = true
                    };

                    await Dispatcher.InvokeAsync(() =>
                    {
                        _clients.Add(clientInfo);
                        UpdateClientCount();
                    });

                    Log($"[+] Client connected: {clientInfo.EndPoint}");

                    // 클라이언트 처리 태스크 시작
                    _ = Task.Run(() => HandleClient(clientInfo, ct));
                }
                catch (ObjectDisposedException)
                {
                    // 서버가 중지됨
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        LogError("Error accepting client", ex);
                    }
                }
            }
        }

        private async Task HandleClient(ClientInfo clientInfo, CancellationToken ct)
        {
            try
            {
                var networkStream = clientInfo.TcpClient.GetStream();
                var buffer = new byte[256];

                while (!ct.IsCancellationRequested && clientInfo.TcpClient.Connected)
                {
                    try
                    {
                        var bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break; // 클라이언트가 연결을 끊음

                        // 간단한 Modbus TCP 프레임 처리
                        await ProcessModbusRequest(buffer, bytesRead, networkStream, clientInfo);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            LogError($"Client {clientInfo.EndPoint} processing error", ex);
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    LogError($"Client {clientInfo.EndPoint} error", ex);
                }
            }
            finally
            {
                await DisconnectClient(clientInfo);
            }
        }

        private async Task ProcessModbusRequest(byte[] buffer, int length, NetworkStream stream, ClientInfo clientInfo)
        {
            try
            {
                // if (length < 8) return;
                //
                // // 응답 지연 시뮬레이션
                // int delay = 0;
                // await Dispatcher.InvokeAsync(() =>
                // {
                //     int.TryParse(txtResponseDelay.Text, out delay);
                // });
                //
                // if (delay > 0)
                // {
                //     await Task.Delay(delay);
                // }

                // DataStore를 통해 Modbus 요청 처리
                var response = _dataStore.ProcessRequest(buffer);
                if (response != null)
                {
                    await stream.WriteAsync(response, 0, response.Length);
                }
        
                // 통계 업데이트
                await Dispatcher.InvokeAsync(() =>
                {
                    _requestCount++;
                    lblRequestCount.Text = _requestCount.ToString();
                });

                // 로깅
                var unitId = buffer[6];
                var functionCode = buffer[7];
                await Dispatcher.InvokeAsync(() =>
                {
                    Log($"[REQ] Unit {unitId} FC{functionCode:X2} from {clientInfo.EndPoint}");
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _errorCount++;
                    lblErrorCount.Text = _errorCount.ToString();
                    LogError($"Error processing request from {clientInfo.EndPoint}", ex);
                });
            }
        }

        private async Task DisconnectClient(ClientInfo clientInfo)
        {
            try
            {
                clientInfo.TcpClient?.Close();
                clientInfo.IsConnected = false;

                await Dispatcher.InvokeAsync(() =>
                {
                    _clients.Remove(clientInfo);
                    UpdateClientCount();
                });

                Log($"[-] Client disconnected: {clientInfo.EndPoint}");
            }
            catch (Exception ex)
            {
                LogError($"Error disconnecting client {clientInfo.EndPoint}", ex);
            }
        }

        private void UpdateServerStatus(bool isRunning, string status)
        {
            Dispatcher.Invoke(() =>
            {
                elServerStatus.Fill = isRunning ? _successBrush : _errorBrush;
                lblServerStatus.Text = status;
                btnToggleServer.Content = isRunning ? "Stop Server" : "Start Server";
                btnToggleServer.Style =
                    isRunning ? (Style)FindResource("StopButton") : (Style)FindResource("PrimaryButton");
            });
        }

        private void UpdateClientCount()
        {
            lblClientCount.Text = _clients.Count(c => c.IsConnected).ToString();
        }

        #endregion
        
        #region UI Update From DataStore
        
        public void UpdateUIFromDataStore(SlaveUnitData unit, int address, ushort value)
        {
            // 현재 표시 중인 Unit과 DataType이 맞는지 확인
            if (unit != _currentUnit || _currentDataType != DataType.HoldingRegisters) return;

            var table = unit.HoldingTable;
            if (table == null) return;

            try
            {
                // 해당 주소의 행 찾기
                foreach (DataRow row in table.Rows)
                {
                    if (row["Address"].ToString() == address.ToString())
                    {
                        // 이벤트 핸들러 일시 제거
                        table.ColumnChanged -= Table_ColumnChanged;
                        
                        // 값 업데이트
                        row["Value"] = value;
                        UpdateRegisterFormats(row, value);
                        
                        // 이벤트 핸들러 다시 연결
                        table.ColumnChanged += Table_ColumnChanged;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Error updating UI from DataStore", ex);
            }
        }

        public void UpdateCoilUIFromDataStore(SlaveUnitData unit, int address, bool value)
        {
            if (unit != _currentUnit || _currentDataType != DataType.Coils) return;

            var table = unit.CoilsTable;
            if (table == null) return;

            try
            {
                foreach (DataRow row in table.Rows)
                {
                    if (row["Address"].ToString() == address.ToString())
                    {
                        table.ColumnChanged -= Table_ColumnChanged;
                        row["Value"] = value;
                        table.ColumnChanged += Table_ColumnChanged;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Error updating coil UI from DataStore", ex);
            }
        }
        
        #endregion
        

        #region Custom DataStore Implementation - Simplified Version

        // 간단한 Modbus 데이터 저장소 (NModbus4 대신 직접 구현)
        public class CustomSlaveDataStore
        {
            private readonly ObservableCollection<SlaveUnitData> _units;
            private readonly Dispatcher _dispatcher; // Dispatcher 저장
            private readonly MainWindow _mainWindow; // MainWindow 참조 추가
            public int ResponseDelay { get; set; } = 0;

            public event EventHandler<ModbusRequestEventArgs> RequestReceived;

            public CustomSlaveDataStore(ObservableCollection<SlaveUnitData> units, Dispatcher dispatcher, MainWindow mainWindow)
            {
                _units = units;
                _dispatcher = dispatcher;
                _mainWindow = mainWindow; // MainWindow 참조 저장
            }

            public byte[] ProcessRequest(byte[] request)
            {
                if (request.Length < 8) return null;

                var unitId = request[6];
                var functionCode = request[7];
                var startAddr = (ushort)((request[8] << 8) | request[9]);
                var count = (ushort)((request[10] << 8) | request[11]);

                OnRequestReceived($"FC{functionCode:X2}", unitId, startAddr, count);

                var unit = _units.FirstOrDefault(u => u.UnitId == unitId);
                if (unit == null)
                {
                    return CreateErrorResponse(request, 0x01); // Illegal Function
                }

                switch (functionCode)
                {
                    case 0x01: // Read Coils
                        return ProcessReadCoils(request, unit, startAddr, count);
                    case 0x02: // Read Discrete Inputs
                        return ProcessReadDiscreteInputs(request, unit, startAddr, count);
                    case 0x03: // Read Holding Registers
                        return ProcessReadHoldingRegisters(request, unit, startAddr, count);
                    case 0x04: // Read Input Registers
                        return ProcessReadInputRegisters(request, unit, startAddr, count);
                    case 0x05: // Write Single Coil
                        return ProcessWriteSingleCoil(request, unit, startAddr);
                    case 0x06: // Write Single Register
                        return ProcessWriteSingleRegister(request, unit, startAddr);
                    case 0x0F: // Write Multiple Coils
                        return ProcessWriteMultipleCoils(request, unit, startAddr, count);
                    case 0x10: // Write Multiple Registers
                        return ProcessWriteMultipleRegisters(request, unit, startAddr, count);
                    default:
                        return CreateErrorResponse(request, 0x01); // Illegal Function
                }
            }

            private byte[] ProcessReadCoils(byte[] request, SlaveUnitData unit, ushort startAddr, ushort count)
            {
                var response = new List<byte>();
                response.AddRange(request.Take(6)); // Copy header
                response.Add(request[6]); // Unit ID
                response.Add(0x01); // Function code

                var byteCount = (byte)((count + 7) / 8);
                response.Add(byteCount);

                for (int i = 0; i < byteCount; i++)
                {
                    byte coilByte = 0;
                    for (int bit = 0; bit < 8 && (i * 8 + bit) < count; bit++)
                    {
                        int addr = startAddr + i * 8 + bit;
                        if (unit.Coils.TryGetValue(addr, out bool value) && value)
                        {
                            coilByte |= (byte)(1 << bit);
                        }
                    }

                    response.Add(coilByte);
                }

                // Update length in header
                var length = (ushort)(response.Count - 6);
                response[4] = (byte)(length >> 8);
                response[5] = (byte)(length & 0xFF);

                return response.ToArray();
            }

            private byte[] ProcessReadDiscreteInputs(byte[] request, SlaveUnitData unit, ushort startAddr, ushort count)
            {
                var response = new List<byte>();
                response.AddRange(request.Take(6)); // Copy header
                response.Add(request[6]); // Unit ID
                response.Add(0x02); // Function code

                var byteCount = (byte)((count + 7) / 8);
                response.Add(byteCount);

                for (int i = 0; i < byteCount; i++)
                {
                    byte inputByte = 0;
                    for (int bit = 0; bit < 8 && (i * 8 + bit) < count; bit++)
                    {
                        int addr = startAddr + i * 8 + bit;
                        if (unit.DiscreteInputs.TryGetValue(addr, out bool value) && value)
                        {
                            inputByte |= (byte)(1 << bit);
                        }
                    }

                    response.Add(inputByte);
                }

                // Update length in header
                var length = (ushort)(response.Count - 6);
                response[4] = (byte)(length >> 8);
                response[5] = (byte)(length & 0xFF);

                return response.ToArray();
            }

            private byte[] ProcessReadHoldingRegisters(byte[] request, SlaveUnitData unit, ushort startAddr,
                ushort count)
            {
                var response = new List<byte>();
                response.AddRange(request.Take(6)); // Copy header
                response.Add(request[6]); // Unit ID
                response.Add(0x03); // Function code
                response.Add((byte)(count * 2)); // Byte count

                for (int i = 0; i < count; i++)
                {
                    int addr = startAddr + i;
                    ushort value = unit.HoldingRegisters.TryGetValue(addr, out ushort regValue) ? regValue : (ushort)0;
                    response.Add((byte)(value >> 8));
                    response.Add((byte)(value & 0xFF));
                }

                // Update length in header
                var length = (ushort)(response.Count - 6);
                response[4] = (byte)(length >> 8);
                response[5] = (byte)(length & 0xFF);

                return response.ToArray();
            }

            private byte[] ProcessReadInputRegisters(byte[] request, SlaveUnitData unit, ushort startAddr, ushort count)
            {
                var response = new List<byte>();
                response.AddRange(request.Take(6)); // Copy header
                response.Add(request[6]); // Unit ID
                response.Add(0x04); // Function code
                response.Add((byte)(count * 2)); // Byte count

                for (int i = 0; i < count; i++)
                {
                    int addr = startAddr + i;
                    ushort value = unit.InputRegisters.TryGetValue(addr, out ushort regValue) ? regValue : (ushort)0;
                    response.Add((byte)(value >> 8));
                    response.Add((byte)(value & 0xFF));
                }

                // Update length in header
                var length = (ushort)(response.Count - 6);
                response[4] = (byte)(length >> 8);
                response[5] = (byte)(length & 0xFF);

                return response.ToArray();
            }

            private byte[] ProcessWriteSingleCoil(byte[] request, SlaveUnitData unit, ushort coilAddr)
            {
                var value = (request[10] << 8) | request[11];
                var coilValue = value == 0xFF00;
                unit.Coils[coilAddr] = coilValue;

                // UI 업데이트 추가
                _dispatcher.InvokeAsync(() =>
                {
                    _mainWindow.UpdateCoilUIFromDataStore(unit, coilAddr, coilValue);
                });

                return request;
            }

            private byte[] ProcessWriteSingleRegister(byte[] request, SlaveUnitData unit, ushort regAddr)
            {
                var value = (ushort)((request[10] << 8) | request[11]);
                unit.HoldingRegisters[regAddr] = value;

                // UI 업데이트 추가
                _dispatcher.InvokeAsync(() =>
                {
                    _mainWindow.UpdateUIFromDataStore(unit, regAddr, value);
                });

                // Echo back the request as response
                return request;
            }

            private byte[] ProcessWriteMultipleCoils(byte[] request, SlaveUnitData unit, ushort startAddr, ushort count)
            {
                var byteCount = request[12];
                var dataStart = 13;

                for (int i = 0; i < count; i++)
                {
                    var byteIndex = i / 8;
                    var bitIndex = i % 8;
                    var coilValue = (request[dataStart + byteIndex] & (1 << bitIndex)) != 0;
                    unit.Coils[startAddr + i] = coilValue;
                }

                // Create response
                var response = new byte[12];
                Array.Copy(request, response, 12);
                return response;
            }

            private byte[] ProcessWriteMultipleRegisters(byte[] request, SlaveUnitData unit, ushort startAddr, ushort count)
            {
                var dataStart = 13;

                for (int i = 0; i < count; i++)
                {
                    var value = (ushort)((request[dataStart + i * 2] << 8) | request[dataStart + i * 2 + 1]);
                    unit.HoldingRegisters[startAddr + i] = value;
                }

                // UI 업데이트 추가 - _mainWindow 사용
                _dispatcher.InvokeAsync(() =>
                {
                    for (int i = 0; i < count; i++)
                    {
                        var addr = startAddr + i;
                        var value = unit.HoldingRegisters[addr];
                        _mainWindow.UpdateUIFromDataStore(unit, addr, value);
                    }
                });

                var response = new byte[12];
                Array.Copy(request, response, 12);
                return response;
            }
            
            

            private byte[] CreateErrorResponse(byte[] request, byte exceptionCode)
            {
                var response = new List<byte>();
                response.AddRange(request.Take(6)); // Copy header
                response.Add(request[6]); // Unit ID
                response.Add((byte)(request[7] | 0x80)); // Function code with error flag
                response.Add(exceptionCode);

                // Update length in header
                response[4] = 0;
                response[5] = 3;

                return response.ToArray();
            }

            private void OnRequestReceived(string function, byte unitId, ushort startAddr, ushort count)
            {
                // UI 스레드에서 실행되도록 보장
                _dispatcher.InvokeAsync(() =>
                {
                    RequestReceived?.Invoke(this, new ModbusRequestEventArgs
                    {
                        Function = function,
                        UnitId = unitId,
                        StartAddress = startAddr,
                        Count = count,
                        Timestamp = DateTime.Now
                    });
                });
            }
        }

        public class ModbusRequestEventArgs : EventArgs
        {
            public string Function { get; set; }
            public byte UnitId { get; set; }
            public ushort StartAddress { get; set; }
            public ushort Count { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion

        
        
        #region Modbus Event Handlers

        private void OnModbusRequestReceived(object sender, ModbusRequestEventArgs e)
        {
            // 이미 UI 스레드에서 호출되므로 Dispatcher 불필요
            _requestCount++;
            lblRequestCount.Text = _requestCount.ToString();
            Log($"[REQ] Unit {e.UnitId} {e.Function} Start:{e.StartAddress} Count:{e.Count}");
        }

        #endregion

        #region Simulation

        // private void btnStartSimulation_Click(object sender, RoutedEventArgs e)
        // {
        //     if (_simulationTimer != null)
        //     {
        //         Log("[!] Simulation is already running.");
        //         return;
        //     }
        //
        //     if (!int.TryParse(txtSimInterval.Text, out var interval) || interval < 100)
        //     {
        //         Log("[!] Simulation interval must be >= 100ms.");
        //         return;
        //     }
        //
        //     _simulationTimer = new Timer(SimulationTick, null, 0, interval);
        //     Log($"[*] Simulation started (interval: {interval}ms)");
        //     UpdateUI();
        // }
        //
        // private void btnStopSimulation_Click(object sender, RoutedEventArgs e)
        // {
        //     _simulationTimer?.Dispose();
        //     _simulationTimer = null;
        //     Log("[*] Simulation stopped");
        //     UpdateUI();
        // }
        //
        // private void SimulationTick(object state)
        // {
        //     try
        //     {
        //         Dispatcher.InvokeAsync(() =>
        //         {
        //             if (chkAutoIncrement.IsChecked == true)
        //             {
        //                 AutoIncrementValues();
        //             }
        //
        //             if (chkRandomNoise.IsChecked == true)
        //             {
        //                 AddRandomNoise();
        //             }
        //         });
        //     }
        //     catch (Exception ex)
        //     {
        //         Dispatcher.InvokeAsync(() => LogError("Simulation error", ex));
        //     }
        // }

        // private void AutoIncrementValues()
        // {
        //     foreach (var unit in _units)
        //     {
        //         // Holding Registers 자동 증가
        //         var addresses = unit.HoldingRegisters.Keys.ToList();
        //         foreach (var addr in addresses)
        //         {
        //             var currentValue = unit.HoldingRegisters[addr];
        //             unit.HoldingRegisters[addr] = (ushort)((currentValue + 1) % 65536);
        //         }
        //
        //         // 테이블 업데이트
        //         if (unit == _currentUnit && _currentDataType == DataType.HoldingRegisters)
        //         {
        //             UpdateTableFromDataStore();
        //         }
        //     }
        // }
        //
        // private void AddRandomNoise()
        // {
        //     foreach (var unit in _units)
        //     {
        //         // Holding Registers에 노이즈 추가
        //         var addresses = unit.HoldingRegisters.Keys.ToList();
        //         foreach (var addr in addresses.Take(Math.Min(5, addresses.Count))) // 최대 5개만
        //         {
        //             var noise = (short)(_random.Next(-10, 11));
        //             var currentValue = (int)unit.HoldingRegisters[addr];
        //             var newValue = Math.Max(0, Math.Min(65535, currentValue + noise));
        //             unit.HoldingRegisters[addr] = (ushort)newValue;
        //         }
        //
        //         // 테이블 업데이트
        //         if (unit == _currentUnit && _currentDataType == DataType.HoldingRegisters)
        //         {
        //             UpdateTableFromDataStore();
        //         }
        //     }
        // }
        //
        // private void UpdateTableFromDataStore()
        // {
        //     if (_currentUnit == null) return;
        //
        //     var table = GetCurrentTable();
        //     if (table == null) return;
        //
        //     try
        //     {
        //         table.ColumnChanged -= Table_ColumnChanged;
        //
        //         foreach (DataRow row in table.Rows)
        //         {
        //             if (int.TryParse(row["Address"].ToString(), out int addr))
        //             {
        //                 switch (_currentDataType)
        //                 {
        //                     case DataType.Coils:
        //                         if (_currentUnit.Coils.TryGetValue(addr, out bool coilValue))
        //                             row["Value"] = coilValue;
        //                         break;
        //                     case DataType.DiscreteInputs:
        //                         if (_currentUnit.DiscreteInputs.TryGetValue(addr, out bool discreteValue))
        //                             row["Value"] = discreteValue;
        //                         break;
        //                     case DataType.HoldingRegisters:
        //                         if (_currentUnit.HoldingRegisters.TryGetValue(addr, out ushort holdingValue))
        //                         {
        //                             row["Value"] = holdingValue;
        //                             UpdateRegisterFormats(row, holdingValue);
        //                         }
        //
        //                         break;
        //                     case DataType.InputRegisters:
        //                         if (_currentUnit.InputRegisters.TryGetValue(addr, out ushort inputValue))
        //                         {
        //                             row["Value"] = inputValue;
        //                             UpdateRegisterFormats(row, inputValue);
        //                         }
        //
        //                         break;
        //                 }
        //             }
        //         }
        //     }
        //     finally
        //     {
        //         table.ColumnChanged += Table_ColumnChanged;
        //     }
        // }

        #endregion

        #region Data Import/Export

        private void btnExportData_Click(object sender, RoutedEventArgs e)
        {
            if (_currentUnit == null)
            {
                Log("[!] No unit selected for export.");
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"Unit{_currentUnit.UnitId}_{_currentDataType}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    ExportToCSV(saveDialog.FileName);
                    Log($"[+] Exported {_currentDataType} data to: {saveDialog.FileName}");
                }
                catch (Exception ex)
                {
                    LogError("Export failed", ex);
                }
            }
        }

        private void btnImportData_Click(object sender, RoutedEventArgs e)
        {
            if (_currentUnit == null)
            {
                Log("[!] No unit selected for import.");
                return;
            }

            var openDialog = new OpenFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
            };

            if (openDialog.ShowDialog() == true)
            {
                try
                {
                    ImportFromCSV(openDialog.FileName);
                    Log($"[+] Imported data from: {openDialog.FileName}");
                }
                catch (Exception ex)
                {
                    LogError("Import failed", ex);
                }
            }
        }

        private void ExportToCSV(string filePath)
        {
            var table = GetCurrentTable();
            if (table == null) return;

            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                // 헤더 작성
                var headers = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName);
                writer.WriteLine(string.Join(",", headers));

                // 데이터 작성
                foreach (DataRow row in table.Rows)
                {
                    var values = row.ItemArray.Select(field =>
                        field?.ToString()?.Contains(",") == true ? $"\"{field}\"" : field?.ToString() ?? "");
                    writer.WriteLine(string.Join(",", values));
                }
            }
        }

        private void ImportFromCSV(string filePath)
        {
            var table = GetCurrentTable();
            if (table == null) return;

            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            if (lines.Length < 2) return;

            var headers = lines[0].Split(',');

            table.Clear();

            for (int i = 1; i < lines.Length; i++)
            {
                var values = ParseCSVLine(lines[i]);
                if (values.Length != headers.Length) continue;

                var row = table.NewRow();
                for (int j = 0; j < headers.Length && j < values.Length; j++)
                {
                    try
                    {
                        var columnName = headers[j].Trim();
                        var value = values[j].Trim().Trim('"');

                        if (table.Columns.Contains(columnName))
                        {
                            var column = table.Columns[columnName];
                            if (column.DataType == typeof(bool))
                                row[columnName] = bool.Parse(value);
                            else if (column.DataType == typeof(ushort))
                                row[columnName] = ushort.Parse(value);
                            else
                                row[columnName] = value;
                        }
                    }
                    catch
                    {
                        // 변환 실패 시 기본값 사용
                    }
                }

                table.Rows.Add(row);
            }

            SyncTableToDataStore();
        }

        private string[] ParseCSVLine(string line)
        {
            var result = new List<string>();
            var inQuotes = false;
            var currentField = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            result.Add(currentField.ToString());
            return result.ToArray();
        }

        private void btnClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_currentUnit == null) return;

            _currentUnit.Coils.Clear();
            _currentUnit.DiscreteInputs.Clear();
            _currentUnit.HoldingRegisters.Clear();
            _currentUnit.InputRegisters.Clear();

            _currentUnit.CoilsTable.Clear();
            _currentUnit.DiscreteTable.Clear();
            _currentUnit.HoldingTable.Clear();
            _currentUnit.InputTable.Clear();

            Log($"[*] Cleared all data for Unit {_currentUnit.UnitId}");
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            // 모든 값을 0으로 리셋
            if (_currentUnit == null) return;

            var table = GetCurrentTable();
            if (table == null) return;

            foreach (DataRow row in table.Rows)
            {
                if (_currentDataType == DataType.Coils || _currentDataType == DataType.DiscreteInputs)
                {
                    row["Value"] = false;
                }
                else
                {
                    row["Value"] = (ushort)0;
                    UpdateRegisterFormats(row, 0);
                }
            }

            SyncTableToDataStore();
            Log($"[*] Reset all values for Unit {_currentUnit.UnitId} {_currentDataType}");
        }

        #endregion

        #region Log Management

        private void btnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(txtLog.Text);
                Log("[*] Log copied to clipboard");
            }
            catch (Exception ex)
            {
                LogError("Failed to copy log", ex);
            }
        }

        private void btnClearLog_Click(object sender, RoutedEventArgs e)
        {
            txtLog.Clear();
        }

        private void btnToggleLog_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLogCollapsed)
            {
                // 접기
                LogPanel.Height = 50; // 헤더만 보이도록
                LogPanel.Padding = new Thickness(2, 9, 2, 0);
                btnToggleLog.Content = "▲";
                txtLog.Visibility = Visibility.Collapsed;
                _isLogCollapsed = true;
            }
            else
            {
                // 펼치기
                LogPanel.Height = 200;
                LogPanel.Padding = new Thickness(12);
                btnToggleLog.Content = "▼";
                txtLog.Visibility = Visibility.Visible;
                _isLogCollapsed = false;
            }
        }

        private void Log(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                txtLog.AppendText($"[{timestamp}] {message}{Environment.NewLine}");
                txtLog.ScrollToEnd();
            });
        }

        private void LogError(string title, Exception ex)
        {
            _errorCount++;
            Dispatcher.InvokeAsync(() => { lblErrorCount.Text = _errorCount.ToString(); });
            Log($"[-] {title}: {ex.Message}");
        }

        #endregion

        #region UI Update Methods

        private void UpdateUI()
        {
            Dispatcher.Invoke(() =>
            {
                // 서버 관련 UI
                txtServerIp.IsEnabled = !_isServerRunning;
                txtServerPort.IsEnabled = !_isServerRunning;

                // 시뮬레이션 관련 UI
                // bool simRunning = _simulationTimer != null;
                // btnStartSimulation.IsEnabled = !simRunning;
                // btnStopSimulation.IsEnabled = simRunning;
                // txtSimInterval.IsEnabled = !simRunning;
            });
        }

        #endregion

        #region Cleanup

        protected override async void OnClosing(CancelEventArgs e)
        {
            try
            {
                // 시뮬레이션 정지
                _simulationTimer?.Dispose();

                // 서버 정지
                if (_isServerRunning)
                {
                    await StopServerAsync();
                }
            }
            catch (Exception ex)
            {
                LogError("Error during cleanup", ex);
            }

            base.OnClosing(e);
        }

        #endregion
    }

    #region Add Range Dialog

    public partial class AddRangeDialog : Window
    {
        public int StartAddress { get; private set; }
        public int Count { get; private set; }

        public AddRangeDialog()
        {
            InitializeComponent();
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtStartAddr.Text, out var start) &&
                int.TryParse(txtCount.Text, out var count) &&
                start >= 0 && start <= 65535 &&
                count > 0 && count <= 1000)
            {
                StartAddress = start;
                Count = count;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show("Invalid input. Start address must be 0-65535, Count must be 1-1000.",
                    "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void InitializeComponent()
        {
            // Window Style
            Title = "Add Address Range";
            Width = 350;
            Height = 240;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;

            // --- Main Container with Shadow ---
            var mainBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1E1E1E")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF444444")),
                BorderThickness = new Thickness(1)
            };
            mainBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 320,
                ShadowDepth = 5,
                Opacity = 0.5,
                BlurRadius = 8
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); // Title bar
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) }); // Buttons
            mainBorder.Child = mainGrid;

            // --- Title Bar ---
            var titleBarGrid = new Grid();
            titleBarGrid.MouseLeftButtonDown += (sender, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            
            Grid.SetRow(titleBarGrid, 0);
            mainGrid.Children.Add(titleBarGrid);
            
            titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = this.Title,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(15, 0, 0, 0),
                FontSize = 14
            };
            Grid.SetColumn(titleText, 0);
            titleBarGrid.Children.Add(titleText);

            var closeButton = new Button
            {
                Content = "✕",
                Foreground = Brushes.Gray,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 40,
                Height = 40,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            closeButton.Click += btnCancel_Click;
            closeButton.MouseEnter += (s, e) => closeButton.Foreground = Brushes.White;
            closeButton.MouseLeave += (s, e) => closeButton.Foreground = Brushes.Gray;
            Grid.SetColumn(closeButton, 1);
            titleBarGrid.Children.Add(closeButton);

            // --- Content Area ---
            var contentGrid = new Grid
            {
                Margin = new Thickness(20, 15, 20, 15)
            };
            Grid.SetRow(contentGrid, 1);
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.VerticalAlignment = VerticalAlignment.Center;

            var lblStart = new TextBlock { Text = "Start Address", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightGray };
            Grid.SetRow(lblStart, 0);
            Grid.SetColumn(lblStart, 0);
            contentGrid.Children.Add(lblStart);

            txtStartAddr = new TextBox
            {
                Text = "0",
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF252526")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF444444")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 34
            };
            Grid.SetRow(txtStartAddr, 0);
            Grid.SetColumn(txtStartAddr, 1);
            contentGrid.Children.Add(txtStartAddr);

            var lblCount = new TextBlock { Text = "Count", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightGray };
            Grid.SetRow(lblCount, 1);
            Grid.SetColumn(lblCount, 0);
            contentGrid.Children.Add(lblCount);

            txtCount = new TextBox
            {
                Text = "10",
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF252526")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF444444")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 34
            };
            Grid.SetRow(txtCount, 1);
            Grid.SetColumn(txtCount, 1);
            contentGrid.Children.Add(txtCount);
            mainGrid.Children.Add(contentGrid);

            // --- Button Area ---
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0)
            };
            Grid.SetRow(buttonPanel, 2);
            mainGrid.Children.Add(buttonPanel);

            // Define colors
            var cancelBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3E3E42"));
            var cancelHoverBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF555555"));
            var okBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF007ACC"));
            var okHoverBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1A8CDE"));
            
            // Freeze brushes for performance
            cancelBg.Freeze();
            cancelHoverBg.Freeze();
            okBg.Freeze();
            okHoverBg.Freeze();

            var btnCancel = new Button
            {
                Content = "Cancel",
                Width = 100,
                Height = 35,
                Margin = new Thickness(5),
                IsCancel = true,
                Background = cancelBg,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            btnCancel.Click += btnCancel_Click;
            btnCancel.MouseEnter += (s, e) => btnCancel.Background = cancelHoverBg;
            btnCancel.MouseLeave += (s, e) => btnCancel.Background = cancelBg;
            buttonPanel.Children.Add(btnCancel);

            var btnOK = new Button
            {
                Content = "OK",
                Width = 100,
                Height = 35,
                Margin = new Thickness(5),
                IsDefault = true,
                Background = okBg,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            btnOK.Click += btnOK_Click;
            btnOK.MouseEnter += (s, e) => btnOK.Background = okHoverBg;
            btnOK.MouseLeave += (s, e) => btnOK.Background = okBg;
            buttonPanel.Children.Add(btnOK);

            this.Content = mainBorder;
        }

        private TextBox txtStartAddr;
        private TextBox txtCount;
    }

    #endregion
}