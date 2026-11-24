using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using GameTranslator.Core;

namespace GameTranslator.App;

/// <summary>Lịch sử bản dịch: xem, sửa tay, dịch lại bằng AI online, xuất CSV.</summary>
public sealed class HistoryWindow : Window
{
    private readonly Session _s;
    private readonly ListBox _list = new() { MinWidth = 380 };
    private readonly TextBox _source = new() { IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 90 };
    private readonly TextBox _target = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 110 };
    private readonly TextBlock _info = Ui.Label("", 12, opacity: 0.7);
    private List<CachedTranslation> _items = [];

    public HistoryWindow(Session s)
    {
        _s = s;
        Title = $"Lịch sử bản dịch — {s.Current.DisplayName}";
        Width = 1000; Height = 640; MinWidth = 700; MinHeight = 400;

        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _items.Count) return;
            var it = _items[_list.SelectedIndex];
            _source.Text = it.Source;
            _target.Text = it.Target;
            _info.Text = $"Nguồn: {it.Engine} · dùng lại {it.Hits} lần · {it.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        };

        var right = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        right.Children.Add(Ui.Label("Chữ gốc", 12, opacity: 0.7));
        right.Children.Add(_source);
        right.Children.Add(Ui.Label("Bản dịch (sửa tay được)", 12, opacity: 0.7));
        right.Children.Add(_target);
        right.Children.Add(_info);
        right.Children.Add(Ui.Horizontal(8,
            Ui.Button("Lưu bản sửa", SaveEdit, accent: true),
            Ui.Button("Dịch lại bằng AI", Retranslate),
            Ui.Button("Xuất CSV", ExportCsv),
            Ui.Button("Xóa lịch sử hồ sơ này", () => { _s.Cache.Clear(_s.Current.Id); Reload(); })));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("420,*") };
        grid.Children.Add(_list);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = grid;
        Reload();
    }

    private void Reload()
    {
        _items = [.. _s.Cache.History(_s.Current.Id, 1000)];
        _list.ItemsSource = _items.Select(i => $"{Shorten(i.Source)}  →  {Shorten(i.Target)}").ToList();
        _source.Text = _target.Text = "";
        _info.Text = _items.Count == 0 ? "Chưa có bản dịch nào cho hồ sơ này." : $"{_items.Count} câu";
    }

    private static string Shorten(string s) => s.Length > 46 ? s[..46].Replace('\n', ' ') + "…" : s.Replace('\n', ' ');

    private CachedTranslation? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _items.Count ? _items[_list.SelectedIndex] : null;

    private void SaveEdit()
    {
        if (Selected is not { } it || string.IsNullOrWhiteSpace(_target.Text)) return;
        _s.Cache.UpdateTarget(it.Key, _target.Text.Trim());
        var idx = _list.SelectedIndex;
        Reload();
        _list.SelectedIndex = idx;
    }

    private async void Retranslate()
    {
        if (Selected is not { } it) return;
        var t = _s.Current.Translate;
        ITranslator tr = t.Provider == TranslateProvider.Claude
            ? new ClaudeTranslator(t.Model, () => Platform.Windows.CredentialStore.Read("claude"), timeoutSeconds: 20)
            : new GeminiTranslator(t.GeminiModel, () => Platform.Windows.CredentialStore.Read("gemini"), timeoutSeconds: 20);
        _info.Text = "Đang dịch lại...";
        try
        {
            var text = await tr.TranslateAsync(new TranslationRequest(it.Source, GlossaryMap: _s.Current.Glossary));
            _s.Cache.UpdateTarget(it.Key, text, tr.Id);
            var idx = _list.SelectedIndex;
            Reload();
            _list.SelectedIndex = idx;
        }
        catch (TranslationException ex)
        {
            _info.Text = ex.Kind == TranslationErrorKind.MissingApiKey ? "Chưa nhập API key (Cài đặt > Dịch)." : "Không dịch lại được: " + ex.Describe();
        }
    }

    private async void ExportCsv()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Xuất lịch sử", SuggestedFileName = $"{_s.Current.Id}-lich-su.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null) return;
        var sb = new StringBuilder("﻿chu_goc,ban_dich,nguon,so_lan_dung,thoi_gian\r\n");
        foreach (var i in _s.Cache.History(_s.Current.Id, 100000))
            sb.Append($"{Csv(i.Source)},{Csv(i.Target)},{Csv(i.Engine)},{i.Hits},{i.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n");
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(sb.ToString()));
        _info.Text = "Đã xuất: " + file.Name;
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
