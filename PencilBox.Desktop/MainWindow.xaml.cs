using System.Windows;
using PencilBox.Desktop.ViewModels;

namespace PencilBox.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
