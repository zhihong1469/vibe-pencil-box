using System.Windows.Controls;
using PencilBox.Desktop.ViewModels;

namespace PencilBox.Desktop.Views;

public partial class SerialTerminalView : UserControl
{
    public SerialTerminalView()
    {
        InitializeComponent();
    }

    public SerialTerminalView(SerialTerminalViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
