// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace ViVe.Studio;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Navigation.Focus();
    }
}
