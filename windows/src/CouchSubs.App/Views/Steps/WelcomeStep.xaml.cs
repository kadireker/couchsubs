// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using CouchSubs.Shell;

namespace CouchSubs.App.Views.Steps;

public partial class WelcomeStep : UserControl
{
    public WelcomeStep() => InitializeComponent();

    /// <summary>PSN, PS5 adımları atlanır; TV adımı isteğe bağlı olarak gösterilir.</summary>
    private void OnUseWindow(object sender, RoutedEventArgs e) => ((AppController)DataContext).UseWindowSource();
}
