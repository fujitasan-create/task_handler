using System.Windows;

namespace task_handler
{
    public partial class MainWindow : Window
    {
        private readonly MetricsViewModel _vm = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
        }
    }
}