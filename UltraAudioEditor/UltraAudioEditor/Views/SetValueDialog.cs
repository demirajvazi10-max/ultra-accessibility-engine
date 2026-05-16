using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace UltraAudioEditor.Views
{
    public partial class SetValueDialog : Window
    {
        public string ResultValue { get; private set; } = "";

        public SetValueDialog(string title, string prompt, string currentValue, string unit = "")
        {
            Width = 380; Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Title = title;
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BrBgDark"];

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            var promptBlock = new System.Windows.Controls.TextBlock
            {
                Text = prompt, FontSize = 12, Margin = new Thickness(0, 0, 0, 8),
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrTextSec"],
                TextWrapping = System.Windows.TextWrapping.Wrap
            };
            System.Windows.Controls.Grid.SetRow(promptBlock, 0);
            grid.Children.Add(promptBlock);

            var inputRow = new System.Windows.Controls.StackPanel
            { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var input = new System.Windows.Controls.TextBox
            {
                Text = currentValue, FontSize = 16, Height = 38, Width = 200,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            input.SetValue(AutomationProperties.NameProperty, prompt);
            input.SelectAll();
            var unitBlock = new System.Windows.Controls.TextBlock
            {
                Text = "  " + unit, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrTextSec"]
            };
            inputRow.Children.Add(input);
            inputRow.Children.Add(unitBlock);
            System.Windows.Controls.Grid.SetRow(inputRow, 1);
            grid.Children.Add(inputRow);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "Otkaži", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Style = (System.Windows.Style)Application.Current.Resources["StdButton"]
            };
            cancelBtn.SetValue(AutomationProperties.NameProperty, "Otkaži");
            cancelBtn.Click += (_, __) => DialogResult = false;

            var okBtn = new System.Windows.Controls.Button
            {
                Content = "Potvrdi", Width = 80, Height = 30, IsDefault = true,
                Style = (System.Windows.Style)Application.Current.Resources["AIButton"]
            };
            okBtn.SetValue(AutomationProperties.NameProperty, "Potvrdi unos");
            okBtn.Click += (_, __) => { ResultValue = input.Text; DialogResult = true; };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            System.Windows.Controls.Grid.SetRow(btnPanel, 4);
            grid.Children.Add(btnPanel);

            Content = grid;
            Loaded += (_, __) => { input.Focus(); input.SelectAll(); };
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) DialogResult = false;
                if (e.Key == Key.Enter) { ResultValue = input.Text; DialogResult = true; }
            };
        }
    }
}
