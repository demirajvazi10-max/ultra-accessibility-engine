using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UltraAudioEditor.Views
{
    public class ProjectStatusWindow : Window
    {
        public ProjectStatusWindow(string statusText)
        {
            Title = "Status projekta — Ultra Audio Editor";
            Width = 600; Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BrBgDark"];

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Status projekta (F6)",
                FontSize = 14, FontWeight = FontWeights.Medium,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"],
                Margin = new Thickness(12, 10, 12, 8)
            };
            title.SetValue(AutomationProperties.HeadingLevelProperty, AutomationHeadingLevel.Level1);
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 12, 0) };
            var txt = new TextBox
            {
                Text = statusText,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = (System.Windows.Media.Brush)Application.Current.Resources["BrBgMid"],
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"],
                BorderThickness = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12, Padding = new Thickness(8)
            };
            txt.SetValue(AutomationProperties.NameProperty, "Status projekta, sve trake i klipovi");
            txt.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Assertive);
            scroll.Content = txt;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var closeBtn = new Button
            {
                Content = "Zatvori (Escape)",
                Style = (System.Windows.Style)Application.Current.Resources["StdButton"],
                Width = 120, Height = 32, Margin = new Thickness(12, 8, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true
            };
            closeBtn.SetValue(AutomationProperties.NameProperty, "Zatvori prozor statusa");
            closeBtn.Click += (_, __) => Close();
            Grid.SetRow(closeBtn, 2);
            grid.Children.Add(closeBtn);

            Content = grid;
            Loaded += (_, __) => { txt.Focus(); txt.SelectAll(); };
            KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) Close();
            };
        }
    }
}
