using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GameTranslator.Core;

namespace GameTranslator.App;

/// <summary>Hồ sơ game: tên, vùng, từ điển; thêm/nhân bản/xóa/nhập/xuất (cùng định dạng JSON với bản macOS).</summary>
public sealed class ProfilesWindow : Window
{
    private readonly Session _s;
    private readonly Action _changed;
    private readonly Action _openRegions;
    private readonly ListBox _list = new();
    private readonly TextBox _name = new() { Width = 360 };
    private readonly TextBox _glossary = new() { AcceptsReturn = true, MinHeight = 160, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
    private readonly TextBlock _regions = Ui.Label("", 12, opacity: 0.8);
    private readonly TextBlock _info = Ui.Label("", 12, opacity: 0.7);
    private bool _loading;

    public ProfilesWindow(Session s, Action changed, Action openRegions)
    {
        _s = s; _changed = changed; _openRegions = openRegions;
        Title = "Hồ sơ game";
        Width = 880; Height = 620; MinWidth = 640; MinHeight = 440;

        _list.SelectionChanged += (_, _) => LoadSelected();
        _name.LostFocus += (_, _) => CommitText();
        _glossary.LostFocus += (_, _) => CommitText();

        var left = new DockPanel { Margin = new Thickness(8) };
        var leftButtons = Ui.Horizontal(6, Ui.Button("Mới", New), Ui.Button("Nhân bản", Duplicate), Ui.Button("Xóa", Delete));
        leftButtons.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        left.Children.Add(_list);

        var right = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        right.Children.Add(Ui.Label("Tên hồ sơ", 12, opacity: 0.7));
        right.Children.Add(_name);
        right.Children.Add(Ui.Label("Vùng hộp thoại", 12, opacity: 0.7));
        right.Children.Add(_regions);
        right.Children.Add(Ui.Button("Chọn vùng trên video...", () => { CommitText(); UseSelected(); _openRegions(); }));
        right.Children.Add(Ui.Label("Từ điển thuật ngữ (mỗi dòng: Tiếng Anh = Tiếng Việt)", 12, opacity: 0.7));
        right.Children.Add(_glossary);
        right.Children.Add(_info);
        right.Children.Add(Ui.Horizontal(8,
            Ui.Button("Dùng hồ sơ này", () => { CommitText(); UseSelected(); }, accent: true),
            Ui.Button("Nhập JSON...", Import),
            Ui.Button("Xuất JSON...", Export)));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(new ScrollViewer { Content = right }.With(sv => Grid.SetColumn(sv, 1)));
        Content = grid;
        Reload(_s.Current.Id);
    }

    private Profile? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _s.ProfileList.Count ? _s.ProfileList[_list.SelectedIndex] : null;

    private void Reload(string? select)
    {
        _s.ReloadProfiles();
        _loading = true;
        _list.ItemsSource = _s.ProfileList.Select(p => p.DisplayName).ToList();
        _list.SelectedIndex = Math.Max(_s.ProfileList.FindIndex(p => p.Id == select), 0);
        _loading = false;
        LoadSelected();
        _changed();
    }

    private void LoadSelected()
    {
        if (_loading || Selected is not { } p) return;
        _loading = true;
        _name.Text = p.DisplayName;
        _glossary.Text = string.Join("\n", p.Glossary.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} = {k.Value}"));
        var r = p.MainRegion;
        _regions.Text = $"Chính: x {r.X:P0}, y {r.Y:P0}, rộng {r.Width:P0}, cao {r.Height:P0}" +
                        string.Concat(p.ExtraRegions.Select((e, i) => $"\nPhụ {i + 1}: x {e.X:P0}, y {e.Y:P0}, rộng {e.Width:P0}, cao {e.Height:P0}"));
        _info.Text = $"Id: {p.Id} · {_s.Cache.Stats(p.Id).Count} câu đã dịch";
        _loading = false;
    }

    private void CommitText()
    {
        if (_loading || Selected is not { } p) return;
        p.Name = string.IsNullOrWhiteSpace(_name.Text) ? p.Id : _name.Text.Trim();
        var g = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (_glossary.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var k = line[..i].Trim(); var v = line[(i + 1)..].Trim();
            if (k.Length > 0) g[k] = v;
        }
        p.Glossary = g;
        _s.Profiles.Save(p);
        var id = p.Id;
        Reload(id);
    }

    private void UseSelected()
    {
        if (Selected is { } p) { _s.SelectProfile(p.Id); _changed(); }
    }

    private void New()
    {
        var baseId = ProfileStore.MakeId("Game mới");
        var id = baseId; int n = 2;
        while (_s.ProfileList.Any(p => p.Id == id)) id = baseId + "-" + n++;
        var p = Profile.Default;
        p.Id = id; p.Name = n > 2 ? $"Game mới {n - 1}" : "Game mới";
        _s.Profiles.Save(p);
        Reload(id);
    }

    private void Duplicate()
    {
        if (Selected is not { } src) return;
        var copy = Profile.FromJson(src.ToJson())!;
        var id = src.Id + "-copy"; int n = 2;
        while (_s.ProfileList.Any(p => p.Id == id)) id = $"{src.Id}-copy{n++}";
        copy.Id = id; copy.Name = src.DisplayName + " (bản sao)";
        _s.Profiles.Save(copy);
        Reload(id);
    }

    private void Delete()
    {
        if (Selected is not { } p || _s.ProfileList.Count <= 1) { _info.Text = "Phải giữ lại ít nhất một hồ sơ."; return; }
        _s.Profiles.Delete(p.Id);
        Reload(_s.ProfileList.FirstOrDefault(x => x.Id != p.Id)?.Id);
        if (_s.Settings.ProfileId == p.Id) _s.SelectProfile(_s.ProfileList[0].Id);
    }

    private async void Import()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Nhập hồ sơ", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;
        try
        {
            var p = _s.Profiles.ImportProfile(files[0].Path.LocalPath);
            Reload(p.Id);
        }
        catch (Exception ex) { _info.Text = "Không nhập được: " + ex.Message; }
    }

    private async void Export()
    {
        if (Selected is not { } p) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Xuất hồ sơ", SuggestedFileName = p.Id + ".json", FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;
        _s.Profiles.Export(p, file.Path.LocalPath);
        _info.Text = "Đã xuất: " + file.Name;
    }
}
