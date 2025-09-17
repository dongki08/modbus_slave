using System.Windows;

namespace modbus_slave
{
    public partial class AddRangeDialog : Window
    {
        public int StartAddress { get; set; }
        public int Quantity { get; set; }

        public AddRangeDialog()
        {
            InitializeComponent();
            // 기본값 설정 (선택 사항)
            txtStartAddress.Text = "0";
            txtQuantity.Text = "10";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtStartAddress.Text, out int startAddress) &&
                int.TryParse(txtQuantity.Text, out int quantity))
            {
                StartAddress = startAddress;
                Quantity = quantity;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show("Please enter valid numbers for Start Address and Quantity.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
