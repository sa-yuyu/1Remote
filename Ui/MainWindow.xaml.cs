using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using _1RM.View.Host.ProtocolHosts;
using Shawn.Utils;

namespace IntegrateContainer
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (sender, args) =>
            {
                ConsoleManager.Show();

                IntegrateHost.ExeFullName = @"C:\Program Files\PuTTY\putty.exe";
                IntegrateHost.ExeArguments = @" 172.20.65.78 -P 22 -l root -pw root";
                IntegrateHost.Start();
            };

            Closed += MainWindow_Closed;
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            IntegrateHost.Close();
        }
    }
}
