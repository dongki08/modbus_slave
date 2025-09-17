# Project Plan

## Task: Style `btnAddRange_Click` Dialog UI

### Initial Plan:
1. Read `modbus_slave/MainWindow.xaml.cs` to find the `btnAddRange_Click` event handler.
2. Analyze how the dialog is created. It's probably a a custom `InputBox.Show`.
3. Create a new XAML file for a custom dialog (`AddRangeDialog.xaml`) with a dark theme and input fields for "Start Address" and "Quantity", and "OK" and "Cancel" buttons.
4. Implement `AddRangeDialog.xaml.cs` to handle input and return `DialogResult`.
5. Modify `btnAddRange_Click` in `MainWindow.xaml.cs` to show the new custom dialog, set its `Owner` to `this`, and handle its `DialogResult`.
6. Add `using modbus_slave;` to `MainWindow.xaml.cs`.

### Completed Steps:
- Created `modbus_slave/AddRangeDialog.xaml` with dark theme styling, input fields for Start Address and Quantity, and OK/Cancel buttons.
- Created `modbus_slave/AddRangeDialog.xaml.cs` to handle input parsing and `DialogResult`.
- Modified `modbus_slave/MainWindow.xaml.cs` to replace `InputBox.Show` with the new `AddRangeDialog`, setting `Owner = this` for centering.
- Added `using modbus_slave;` to `modbus_slave/MainWindow.xaml.cs`.
- **Fixed compilation errors:**
    - Changed `StartAddress` and `Quantity` properties in `AddRangeDialog.xaml.cs` from `private set` to `public set`.
    - Removed the old `AddRangeDialog` definition from `MainWindow.xaml.cs`.
    - Corrected `btnAddRange_Click` in `MainWindow.xaml.cs` to use `this._currentUnit.UnitId` instead of `_selectedUnitId` and explicitly referenced `this._modbusDataStore`.
    - Added `AddCoilRange`, `AddDiscreteInputRange`, `AddHoldingRegisterRange`, and `AddInputRegisterRange` methods to the `CustomSlaveDataStore` class in `MainWindow.xaml.cs`.