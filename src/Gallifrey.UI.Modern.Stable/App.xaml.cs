﻿using Gallifrey.Versions;
using System.Windows;

namespace Gallifrey.UI.Modern.Stable
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Modern.App.Run(InstanceType.Stable);
        }
    }
}
