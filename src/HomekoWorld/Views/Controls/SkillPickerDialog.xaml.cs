using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HomekoWorld.Models;
using HomekoWorld.Services;
using HomekoWorld.Services.Skills;

namespace HomekoWorld.Views.Controls;

public partial class SkillPickerDialog : Window
{
    private readonly SkillSlotMap _slotMap;
    private readonly SkillLibrary _library;

    public string? SelectedSkillId { get; private set; }

    public SkillPickerDialog(SkillSlotMap slotMap, SkillLibrary library)
    {
        InitializeComponent();
        _slotMap = slotMap;
        _library = library;
        BuildTabs();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void BuildTabs()
    {
        BarTabs.Items.Clear();

        if (_slotMap.CalibratedAt is not null)
        {
            BuildBarTabs();
            BuildLibraryTab();
        }
        else
        {
            BuildLibraryTabs();
        }
    }

    // Kalibre edilmiş mod: F-bar sekmeleri + kütüphane sekmesi
    private void BuildBarTabs()
    {
        var ghostStyle = (Style)Application.Current.Resources["BtnGhost"];
        var gold       = (Brush)Application.Current.Resources["Gold"];
        var textDim    = (Brush)Application.Current.Resources["TextDim"];
        var slotKeys   = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

        for (int bar = 1; bar <= _slotMap.BarCount; bar++)
        {
            var tab   = new TabItem { Header = $"F{bar}" };
            var panel = new WrapPanel { Margin = new Thickness(8) };

            for (int slotIdx = 0; slotIdx < _slotMap.SlotCount; slotIdx++)
            {
                var slotChar = slotIdx < slotKeys.Length ? slotKeys[slotIdx] : (slotIdx + 1).ToString();
                var key      = $"F{bar}.{slotChar}";

                bool hasSkill = _slotMap.Slots.TryGetValue(key, out var skillId) && !string.IsNullOrEmpty(skillId);
                var skill     = hasSkill ? _library.GetById(skillId!) : null;

                var btn = new Button
                {
                    Width     = 64,
                    Height    = 64,
                    Margin    = new Thickness(3),
                    ToolTip   = skill?.NameTr ?? $"{key} — Boş",
                    Tag       = hasSkill ? skillId : null,
                    IsEnabled = hasSkill,
                    Style     = ghostStyle,
                };
                btn.Click += SlotButton_Click;

                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

                if (skill is not null)
                {
                    var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Skills", skill.IconPath);
                    if (File.Exists(iconPath))
                    {
                        var img = new Image { Width = 36, Height = 36, Stretch = Stretch.Uniform };
                        try { img.Source = new BitmapImage(new Uri(iconPath)); } catch { }
                        content.Children.Add(img);
                    }
                    content.Children.Add(new TextBlock
                    {
                        Text                = slotChar,
                        FontSize            = 10,
                        Foreground          = gold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                }
                else
                {
                    content.Children.Add(new TextBlock
                    {
                        Text                = slotChar,
                        FontSize            = 14,
                        Foreground          = textDim,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                    });
                }

                btn.Content = content;
                panel.Children.Add(btn);
            }

            tab.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            BarTabs.Items.Add(tab);
        }

        if (BarTabs.Items.Count > 0)
            BarTabs.SelectedIndex = 0;
    }

    private void BuildLibraryTab()
    {
        var tab = new TabItem { Header = "📚 Kütüphane" };
        tab.Content = new ScrollViewer
        {
            Content = BuildLibraryContent(),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        BarTabs.Items.Add(tab);
    }

    private void BuildLibraryTabs()
    {
        var classOrder = new[] { "warrior", "mage", "priest", "rogue" };
        var classNames = new Dictionary<string, string>
        {
            ["warrior"] = "Savaşçı",
            ["mage"]    = "Büyücü",
            ["priest"]  = "Rahip",
            ["rogue"]   = "Serseri",
        };

        var groups = _library.All
            .GroupBy(s => s.Class)
            .OrderBy(g =>
            {
                var idx = Array.IndexOf(classOrder, g.Key);
                return idx >= 0 ? idx : 999;
            });

        foreach (var group in groups)
        {
            var displayName = classNames.TryGetValue(group.Key, out var n) ? n : group.Key;
            var tab         = new TabItem { Header = displayName };
            tab.Content = new ScrollViewer
            {
                Content = BuildClassPanel(group),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            BarTabs.Items.Add(tab);
        }

        if (BarTabs.Items.Count > 0)
            BarTabs.SelectedIndex = 0;
    }

    private UIElement BuildLibraryContent()
    {
        var classOrder = new[] { "warrior", "mage", "priest", "rogue" };
        var classNames = new Dictionary<string, string>
        {
            ["warrior"] = "Savaşçı",
            ["mage"]    = "Büyücü",
            ["priest"]  = "Rahip",
            ["rogue"]   = "Serseri",
        };
        var gold = (Brush)Application.Current.Resources["Gold"];

        var outer = new StackPanel { Margin = new Thickness(4) };

        var groups = _library.All
            .GroupBy(s => s.Class)
            .OrderBy(g =>
            {
                var idx = Array.IndexOf(classOrder, g.Key);
                return idx >= 0 ? idx : 999;
            });

        foreach (var group in groups)
        {
            var displayName = classNames.TryGetValue(group.Key, out var n) ? n : group.Key;

            outer.Children.Add(new TextBlock
            {
                Text       = displayName.ToUpperInvariant(),
                FontSize   = 10,
                Foreground = gold,
                Margin     = new Thickness(6, 10, 6, 2),
                FontWeight = FontWeights.SemiBold,
            });

            outer.Children.Add(BuildClassPanel(group));
        }

        return outer;
    }

    private WrapPanel BuildClassPanel(IEnumerable<SkillInfo> skills)
    {
        var ghostStyle = (Style)Application.Current.Resources["BtnGhost"];
        var textMuted  = (Brush)Application.Current.Resources["TextMuted"];
        var panel      = new WrapPanel { Margin = new Thickness(4) };

        foreach (var skill in skills.OrderBy(s => s.NameTr))
            panel.Children.Add(CreateSkillButton(skill, ghostStyle, textMuted));

        return panel;
    }

    private Button CreateSkillButton(SkillInfo skill, Style ghostStyle, Brush textMuted)
    {
        var btn = new Button
        {
            Width   = 64,
            Height  = 64,
            Margin  = new Thickness(3),
            ToolTip = skill.NameTr,
            Tag     = skill.Id,
            Style   = ghostStyle,
        };
        btn.Click += SlotButton_Click;

        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Skills", skill.IconPath);
        if (File.Exists(iconPath))
        {
            var img = new Image { Width = 38, Height = 38, Stretch = Stretch.Uniform };
            try { img.Source = new BitmapImage(new Uri(iconPath)); } catch { }
            content.Children.Add(img);
        }

        var label = skill.NameTr.Length > 9 ? skill.NameTr[..8] + "…" : skill.NameTr;
        content.Children.Add(new TextBlock
        {
            Text                = label,
            FontSize            = 9,
            Foreground          = textMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment       = TextAlignment.Center,
            Width               = 58,
            TextWrapping        = TextWrapping.NoWrap,
        });

        btn.Content = content;
        return btn;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();

        if (string.IsNullOrEmpty(query))
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            BarTabs.Visibility     = Visibility.Visible;
            return;
        }

        BarTabs.Visibility     = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Visible;

        SearchResults.Children.Clear();

        var ghostStyle = (Style)Application.Current.Resources["BtnGhost"];
        var textMuted  = (Brush)Application.Current.Resources["TextMuted"];

        var matches = _library.All
            .Where(s => s.NameTr.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (s.NameEn?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(s => s.NameTr);

        foreach (var skill in matches)
            SearchResults.Children.Add(CreateSkillButton(skill, ghostStyle, textMuted));
    }

    private void SlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string skillId)
        {
            SelectedSkillId = skillId;
            DialogResult    = true;
        }
    }

    private void BarTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
