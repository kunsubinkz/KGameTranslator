using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GameTranslator.Core;
using GameTranslator.Core.LocalHost;
using GameTranslator.Platform.Windows.LocalModel;

namespace GameTranslator.App;

/// <summary>Màn hình "Dịch offline (NLLB-200)": cài một nút, chọn model, quản lý host, chạy thử và đo tốc độ.</summary>
public sealed class LocalModelWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#2F7BFF"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#3DDC84"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#FFB347"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#8A8A94"));
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#26262B"));
    private static readonly IBrush CardLine = new SolidColorBrush(Color.Parse("#3A3A42"));
    private static readonly IBrush ChipBg = new SolidColorBrush(Color.Parse("#34343C"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#A9A9B2"));

    private readonly Session _s;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // Đầu trang
    private readonly Ellipse _pillDot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pillText = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _pill = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6), Background = ChipBg, VerticalAlignment = VerticalAlignment.Center };

    // Khung chưa cài và khung tiến trình
    private readonly StackPanel _hero = new() { Spacing = 14 };
    private readonly Border _jobCard = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Background = CardBg, BorderBrush = CardLine, BorderThickness = new Thickness(1) };
    private readonly TextBlock _jobTitle = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _jobText = new() { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _jobError = new() { FontSize = 12, Foreground = Bad, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _jobProgress = new() { Minimum = 0, Maximum = 1, Height = 8 };
    private readonly Button _jobCancel;

    // Khung chính khi đã cài
    private readonly StackPanel _main = new() { Spacing = 14 };
    private readonly TextBlock _hostLine = new() { FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _envLine = new() { FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hostError = new() { FontSize = 12, Foreground = Bad, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _modelList = new() { Spacing = 8 };
    private readonly TextBlock _modelsDir = new() { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _modelMsg = new() { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _testResult = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _benchResult = new() { FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _logBox = new() { IsReadOnly = true, AcceptsReturn = true, Height = 170, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Text)> _cardProgress = [];
    private string _modelsSig = "";
    private bool _benchRunning;

    private static readonly string[] SampleSentences =
    [
        "Hello, traveler! Where are you headed?", "I can't believe you made it this far.", "The ancient door won't open without the key.",
        "Be careful, the bridge is not safe anymore.", "Thank you for saving our village.", "What do you want me to do with this?",
        "We need to find the princess before sunset.", "That sword looks dangerous. Are you sure about this?", "Please, take this potion. You'll need it.",
        "The monsters are getting stronger every night.", "I've been waiting for you for a very long time.", "Don't give up now. We're almost there!",
        "This place feels strangely familiar.", "Come back when you've collected ten crystals.", "Our only hope is to reach the castle by dawn.",
        "You look tired. Why don't you rest at the inn?", "I'll never forgive what they did to my family.", "Wait, did you hear that noise?",
        "The treasure is hidden beyond the frozen lake.", "Good luck out there, and stay safe.",
    ];

    public LocalModelWindow(Session s)
    {
        _s = s;
        Title = "Dịch offline (NLLB-200)";
        Width = 860; Height = 900; MinWidth = 680; MinHeight = 560;

        _jobCancel = Ui.Button("Hủy", () => _s.Jobs.Cancel());
        _jobCancel.HorizontalAlignment = HorizontalAlignment.Left;
        _jobCard.Child = new StackPanel { Spacing = 10, Children = { _jobTitle, _jobProgress, _jobText, _jobError, _jobCancel } };

        BuildHero();
        BuildMain();

        _pill.Child = Ui.Horizontal(8, _pillDot, _pillText);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Dịch offline", FontSize = 24, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "NLLB-200 chạy ngay trên máy bạn, không cần mạng, không tốn API.", FontSize = 13, Foreground = Dim },
            },
        });
        Grid.SetColumn(_pill, 1);
        header.Children.Add(_pill);

        var licence = new TextBlock
        {
            Text = "Model NLLB-200 theo giấy phép CC-BY-NC 4.0 (phi thương mại). App không đóng gói model; model được tải từ Hugging Face về máy bạn.",
            FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
        };

        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(header);
        root.Children.Add(_jobCard);
        root.Children.Add(_hero);
        root.Children.Add(_main);
        root.Children.Add(licence);
        Content = Ui.Scroll(root);

        _s.Jobs.Changed += OnJobChanged;
        _s.LocalHost.StatusChanged += OnHostChanged;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _s.Jobs.Changed -= OnJobChanged; _s.LocalHost.StatusChanged -= OnHostChanged; };
        Refresh();
    }

    private void OnJobChanged() => Dispatcher.UIThread.Post(Refresh);
    private void OnHostChanged(HostStatus _) => Dispatcher.UIThread.Post(Refresh);

    // ---------- Thành phần dựng sẵn ----------

    private static Border Card(Control child, string? title = null, string? subtitle = null, Control? right = null)
    {
        var body = new StackPanel { Spacing = 12 };
        if (title is not null)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var t = new StackPanel { Spacing = 2 };
            t.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
            if (subtitle is not null) t.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
            head.Children.Add(t);
            if (right is not null) { Grid.SetColumn(right, 1); head.Children.Add(right); }
            body.Children.Add(head);
        }
        body.Children.Add(child);
        return new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Background = CardBg,
            BorderBrush = CardLine, BorderThickness = new Thickness(1), Child = body,
        };
    }

    private static Border Chip(string text, IBrush? fg = null) => new()
    {
        CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), Background = ChipBg, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = fg ?? Dim },
    };

    private static Button Pill(string text, Action onClick, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(14, 7), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
        };
        if (primary) b.Classes.Add("accent");
        if (danger) b.Foreground = Bad;
        b.Click += (_, _) => onClick();
        return b;
    }

    private static string Gb(long bytes) => $"{bytes / 1073741824.0:0.#} GB";

    private static double FreeGb(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace / 1073741824.0; }
        catch (Exception) { return 0; }
    }

    // ---------- Khung "chưa cài" ----------

    private void BuildHero()
    {
        var l = _s.Settings.Local;
        var info = LocalModelCatalog.Find(l.ModelId);
        var auto = LocalJobs.BackendFor(_s);
        var selected = l.Backend is "xpu" or "cuda" or "cpu" ? l.Backend : "auto";

        string GpuName(string b) => b switch { "xpu" => "Intel Arc", "cuda" => "NVIDIA", _ => "CPU" };
        var options = new (string Id, string Title, string Sub)[]
        {
            ("auto", "Tự chọn", $"Theo card màn hình: {GpuName(auto)}"),
            ("xpu", "Intel Arc", "GPU Intel (XPU)"),
            ("cuda", "NVIDIA", "GPU NVIDIA (CUDA)"),
            ("cpu", "CPU", "Chậm hơn, luôn chạy được"),
        };
        var chosen = selected;
        var optionRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var tiles = new List<(string Id, Border Tile)>();
        void Paint() { foreach (var (id, tile) in tiles) tile.BorderBrush = id == chosen ? Accent : CardLine; }
        foreach (var (id, title, sub) in options)
        {
            var tile = new Border
            {
                Width = 190, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10),
                Background = ChipBg, BorderThickness = new Thickness(2), Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = sub, FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            var captured = id;
            tile.PointerPressed += (_, _) => { chosen = captured; Paint(); };
            tiles.Add((id, tile));
            optionRow.Children.Add(tile);
        }
        Paint();

        var envGb = auto == "cpu" ? 1.2 : 3.5;
        var total = envGb + (info?.DownloadBytes ?? 0) / 1073741824.0;
        var steps = new StackPanel { Spacing = 6 };
        void Step(string n, string text) => steps.Children.Add(Ui.Horizontal(10,
            new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Accent, Child = new TextBlock { Text = n, FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 }));
        Step("1", "Cài Python và PyTorch riêng cho app (không đụng Python của bạn)");
        Step("2", $"Tải model {info?.Title ?? l.ModelId} ({Gb(info?.DownloadBytes ?? 0)})");
        Step("3", "Khởi động host dịch và dịch thử một câu");

        var hero = new StackPanel { Spacing = 14 };
        hero.Children.Add(new TextBlock { Text = "Dịch offline chưa được cài", FontSize = 18, FontWeight = FontWeight.SemiBold });
        hero.Children.Add(new TextBlock
        {
            Text = $"Một lần bấm, app tự làm hết. Cần tải khoảng {total:0.#} GB vào {AppPaths.Nllb}. Ổ đĩa còn trống {FreeGb(AppPaths.Local):0.#} GB. Tắt app giữa chừng thì lần sau bấm lại sẽ làm tiếp.",
            FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
        });
        hero.Children.Add(steps);
        hero.Children.Add(new TextBlock { Text = "Chạy bằng", FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 6, 0, 0) });
        hero.Children.Add(optionRow);
        hero.Children.Add(Ui.Horizontal(10,
            Pill("Cài đặt dịch offline", () => _ = _s.Jobs.InstallAllAsync(chosen == "auto" ? null : chosen), primary: true),
            Pill("Đổi thư mục lưu...", async () => await PickModelsDir())));
        _hero.Children.Add(Card(hero));
    }

    // ---------- Khung chính ----------

    private void BuildMain()
    {
        // Host
        var host = new StackPanel { Spacing = 10 };
        host.Children.Add(_hostLine);
        host.Children.Add(_hostError);
        host.Children.Add(Ui.Horizontal(8,
            Pill("Khởi động", () => _ = _s.LocalHost.StartAsync(), primary: true),
            Pill("Dừng", () => _ = _s.LocalHost.StopAsync()),
            Pill("Khởi động lại", () => _ = _s.LocalHost.RestartAsync())));
        host.Children.Add(Ui.Check("Tự khởi động khi mở app và cần dịch offline", _s.Settings.Local.AutoStart, v => { _s.Settings.Local.AutoStart = v; _s.Settings.Save(); }));
        _main.Children.Add(Card(host, "Host dịch", "Tiến trình Python chạy nền; tự tắt khi bạn thoát app."));

        // Model
        var repoBox = new TextBox { Width = 300, Watermark = "owner/tên-model (bản fine-tune NLLB)" };
        var models = new StackPanel { Spacing = 10 };
        models.Children.Add(_modelsDir);
        models.Children.Add(_modelList);
        models.Children.Add(_modelMsg);
        models.Children.Add(new WrapPanel
        {
            Children =
            {
                Wrap(Pill("Đổi thư mục lưu...", async () => await PickModelsDir())),
                Wrap(Pill("Nhập model có sẵn", ImportModels)),
                Wrap(repoBox),
                Wrap(Pill("Thêm repo", () =>
                {
                    var r = repoBox.Text?.Trim();
                    if (string.IsNullOrEmpty(r) || !r.Contains('/') || _s.Settings.Local.ExtraModels.Contains(r)) return;
                    _s.Settings.Local.ExtraModels.Add(r); _s.Settings.Save(); repoBox.Text = ""; _modelsSig = ""; Refresh();
                })),
            },
        });
        _main.Children.Add(Card(models, "Model", "Chọn model dịch. Model lớn dịch tốt hơn nhưng chậm hơn và cần nhiều VRAM hơn."));

        // Môi trường
        var env = new StackPanel { Spacing = 10 };
        env.Children.Add(_envLine);
        var device = Ui.Combo(
            [new("auto", "Tự động"), new("xpu", "GPU Intel (xpu)"), new("cuda", "GPU NVIDIA (cuda)"), new("cpu", "CPU")],
            _s.Settings.Local.Device, c => { _s.Settings.Local.Device = c.Id; _s.LocalHost.Device = c.Id; _s.Settings.Save(); }, 220);
        env.Children.Add(Ui.Horizontal(10, new TextBlock { Text = "Thiết bị chạy", VerticalAlignment = VerticalAlignment.Center }, device,
            new TextBlock { Text = "Đổi xong bấm Khởi động lại.", FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center }));
        env.Children.Add(Ui.Horizontal(8,
            Pill("Cài lại", () => _ = _s.Jobs.ReinstallEnvAsync(LocalJobs.BackendFor(_s))),
            Pill("Gỡ môi trường", async () => { await _s.LocalHost.StopAsync(); _s.LocalHost.Environment.Uninstall(); _s.LocalHost.Refresh(); Refresh(); }, danger: true)));
        _main.Children.Add(Card(env, "Môi trường"));

        // Chạy thử
        var input = new TextBox { Text = "Hello, traveler! Where are you headed?", MinWidth = 400, Height = 36, VerticalContentAlignment = VerticalAlignment.Center };
        var test = new StackPanel { Spacing = 10 };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(input);
        var go = Pill("Dịch", async () => await TryTranslate(input.Text ?? ""), primary: true);
        go.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(go, 1);
        row.Children.Add(go);
        test.Children.Add(row);
        test.Children.Add(_testResult);
        test.Children.Add(Ui.Horizontal(8,
            Pill("Đo tốc độ (20 câu mẫu)", () => _ = Bench(SampleSentences)),
            Pill("Đo bằng file...", async () => await BenchFile())));
        test.Children.Add(_benchResult);
        _main.Children.Add(Card(test, "Chạy thử và đo tốc độ", "Mục tiêu: dưới khoảng 300 ms mỗi câu."));

        // Log
        var log = new StackPanel { Spacing = 8 };
        log.Children.Add(_logBox);
        log.Children.Add(Pill("Mở thư mục log", () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true }); } catch (Exception) { }
        }));
        _main.Children.Add(new Expander { Header = "Log host", Content = log, HorizontalAlignment = HorizontalAlignment.Stretch });
    }

    private static Control Wrap(Control c) { c.Margin = new Thickness(0, 0, 8, 8); return c; }

    // ---------- Làm mới ----------

    private void Refresh()
    {
        var j = _s.Jobs;
        var installed = _s.LocalHost.Environment.IsInstalled;
        var showJob = j.Busy || j.Error is not null || j.Text.StartsWith("Đã hủy");
        // Khi đang cài môi trường thì chỉ hiện khung tiến trình; tải model lẻ thì hiện ngay trên thẻ model.
        var jobOnCard = installed && j.CurrentRepo is not null && j.Busy;
        _jobCard.IsVisible = showJob && !jobOnCard;
        _jobTitle.Text = j.Title;
        _jobText.Text = j.Text;
        _jobProgress.IsIndeterminate = j.Fraction is null;
        _jobProgress.Value = j.Fraction ?? 0;
        _jobProgress.IsVisible = j.Busy;
        _jobCancel.IsVisible = j.Busy;
        _jobError.Text = j.Error ?? "";
        _jobError.IsVisible = j.Error is not null;

        _hero.IsVisible = !installed && !j.Busy;
        _main.IsVisible = installed;

        var st = _s.LocalHost.Status;
        var color = st.State switch { HostState.Ready => Good, HostState.Error => Bad, HostState.Loading or HostState.Starting => Warn, _ => Idle };
        _pillDot.Fill = color;
        _pillText.Text = _s.Settings.Local.Host == LocalHostKind.External ? "Host ngoài" : st.State == HostState.Error ? "Host gặp lỗi" : st.Text;
        _hostLine.Text = st.Device is { Length: > 0 }
            ? $"{st.Model}  ·  {st.Device}  ·  cổng {_s.LocalHost.Port}"
            : "Chưa chạy. Bấm Khởi động, hoặc để app tự chạy khi cần dịch offline.";
        _hostError.Text = st.State == HostState.Error ? st.Text : "";
        _hostError.IsVisible = st.State == HostState.Error;

        var env = _s.LocalHost.Environment.Info();
        _envLine.Text = env is null ? "Chưa cài." : $"Python {env.Python}  ·  torch {env.Torch}  ·  gói {env.Backend}" +
            (env.DeviceName is { Length: > 0 } ? $"  ·  {env.DeviceName}" : env.Xpu || env.Cuda ? "" : "  ·  không thấy GPU, sẽ chạy bằng CPU");
        _modelsDir.Text = "Thư mục lưu: " + _s.LocalHost.Paths.ModelsDir + $"  (còn trống {FreeGb(_s.LocalHost.Paths.ModelsDir):0.#} GB)";

        var all = LocalModelCatalog.Models
            .Select(m => (RepoId: m.RepoId, Title: m.Title, Note: m.Note, Bytes: m.DownloadBytes, Vram: m.VramGb))
            .Concat(_s.Settings.Local.ExtraModels.Select(r => (RepoId: r, Title: r, Note: "Thêm tay", Bytes: 0L, Vram: 0.0))).ToList();
        var sig = string.Join(",", all.Select(m => _s.LocalHost.ModelDownloaded(m.RepoId))) + _s.Settings.Local.ModelId + j.Busy + j.CurrentRepo;
        if (sig != _modelsSig)
        {
            _modelsSig = sig;
            _modelList.Children.Clear();
            _cardProgress.Clear();
            foreach (var m in all) _modelList.Children.Add(ModelCard(m.RepoId, m.Title, m.Note, m.Bytes, m.Vram));
        }
        foreach (var (repo, (bar, text)) in _cardProgress)
        {
            bar.IsIndeterminate = j.Fraction is null;
            bar.Value = j.Fraction ?? 0;
            text.Text = j.Text;
        }
        _logBox.Text = string.Join("\n", _s.LocalHost.LogTail().TakeLast(80));
    }

    private Control ModelCard(string repo, string title, string note, long bytes, double vram)
    {
        var downloaded = _s.LocalHost.ModelDownloaded(repo);
        var selected = _s.Settings.Local.ModelId == repo;
        var downloading = _s.Jobs.Busy && _s.Jobs.CurrentRepo == repo;

        var radio = new Ellipse
        {
            Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, StrokeThickness = 2,
            Stroke = selected ? Accent : downloaded ? Dim : CardLine,
            Fill = selected ? Accent : Brushes.Transparent,
        };
        var name = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold });
        var chips = new WrapPanel();
        chips.Children.Add(Chip(note));
        if (bytes > 0) chips.Children.Add(Chip("Tải " + Gb(bytes)));
        if (vram > 0) chips.Children.Add(Chip($"~{vram:0.#} GB VRAM"));
        foreach (var c in chips.Children) c.Margin = new Thickness(0, 0, 6, 0);
        name.Children.Add(chips);

        Control action;
        if (downloading) action = Pill("Hủy", () => _s.Jobs.Cancel());
        else if (selected && downloaded) action = Chip("Đang dùng", Good);
        else if (downloaded)
        {
            var del = Pill("Xóa", async () =>
            {
                if (_s.LocalHost.IsRunning && _s.Settings.Local.ModelId == repo) await _s.LocalHost.StopAsync();
                try { Directory.Delete(_s.LocalHost.Paths.ModelDir(repo), true); } catch (Exception ex) { _modelMsg.Text = "Không xóa được: " + ex.Message; }
                _s.Settings.Local.ModelPaths.Remove(repo);
                _s.LocalHost.Refresh(); _modelsSig = ""; Refresh();
            }, danger: true);
            action = Ui.Horizontal(8, Pill("Dùng", () => Select(repo), primary: true), del);
        }
        else
        {
            action = Pill(bytes > 0 ? $"Tải ({Gb(bytes)})" : "Tải", () => _ = _s.Jobs.DownloadModelAsync(repo), primary: true);
            action.IsEnabled = !_s.Jobs.Busy;
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(radio);
        name.Margin = new Thickness(12, 0);
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        Grid.SetColumn(action, 2);
        row.Children.Add(action);

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(row);
        if (downloading)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
            var text = new TextBlock { FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
            _cardProgress[repo] = (bar, text);
            body.Children.Add(bar);
            body.Children.Add(text);
        }
        var card = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12), Background = ChipBg, Child = body,
            BorderThickness = new Thickness(2), BorderBrush = selected && downloaded ? Accent : Brushes.Transparent,
        };
        if (downloaded && !selected)
        {
            card.Cursor = new Cursor(StandardCursorType.Hand);
            card.PointerPressed += (_, e) => { if (e.Source is not Button and not TextBlock { Parent: Button }) Select(repo); };
        }
        return card;
    }

    private void Select(string repo)
    {
        if (_s.Settings.Local.ModelId == repo) return;
        _s.Settings.Local.ModelId = repo;
        _s.Settings.Save();
        _s.LocalHost.Refresh();
        if (_s.LocalHost.IsRunning) _ = _s.LocalHost.RestartAsync();
        _modelsSig = ""; Refresh();
    }

    // ---------- Hành động ----------

    private async Task PickModelsDir()
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Chọn thư mục lưu model", AllowMultiple = false });
        if (r.Count == 0 || r[0].TryGetLocalPath() is not { } path) return;
        _s.Settings.Local.ModelsDir = path;
        _s.Settings.Save();
        _s.ReconfigureLocalHost();
        _modelsSig = ""; Refresh();
    }

    private void ImportModels()
    {
        var found = 0;
        foreach (var m in LocalModelCatalog.Models)
        {
            if (_s.LocalHost.ModelDownloaded(m.RepoId)) continue;
            if (ModelDownloader.FindInHfCache(m.RepoId) is { } dir && ModelDownloader.IsComplete(dir))
            {
                _s.Settings.Local.ModelPaths[m.RepoId] = dir;
                found++;
            }
        }
        _s.Settings.Save();
        if (found > 0) _s.ReconfigureLocalHost();
        _modelMsg.Text = found > 0 ? $"Đã nhập {found} model từ cache Hugging Face (dùng tại chỗ, không chép)." : "Không thấy model NLLB nào trong cache Hugging Face.";
        _modelsSig = ""; Refresh();
    }

    private async Task TryTranslate(string text)
    {
        _testResult.Foreground = Dim;
        _testResult.Text = "Đang dịch...";
        if (_s.OfflineTranslator() is not { } tr) { _testResult.Text = "Dịch offline đang tắt trong Cài đặt > Dịch."; return; }
        try
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var r = await tr.TranslateAsync(new TranslationRequest(text));
            _testResult.Foreground = null;
            _testResult.Text = $"{r}   ({System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms)";
        }
        catch (TranslationException ex) { _testResult.Foreground = Bad; _testResult.Text = "Lỗi: " + ex.Message; }
    }

    private async Task BenchFile()
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Chọn file câu (mỗi dòng một câu)", AllowMultiple = false });
        if (r.Count == 0 || r[0].TryGetLocalPath() is not { } path) return;
        await Bench(File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList());
    }

    private async Task Bench(IReadOnlyList<string> sentences)
    {
        if (_benchRunning || sentences.Count == 0) return;
        if (_s.OfflineTranslator() is not { } tr) { _benchResult.Text = "Dịch offline đang tắt."; return; }
        _benchRunning = true;
        var times = new List<double>();
        try
        {
            foreach (var (s, i) in sentences.Select((s, i) => (s, i)))
            {
                _benchResult.Text = $"Đang đo {i + 1}/{sentences.Count}...";
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await tr.TranslateAsync(new TranslationRequest(s));
                times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            times.Sort();
            _benchResult.Text = $"{times.Count} câu: TB {times.Average():0} ms · trung vị {times[times.Count / 2]:0} · nhanh nhất {times[0]:0} · chậm nhất {times[^1]:0}";
        }
        catch (TranslationException ex) { _benchResult.Text = "Lỗi: " + ex.Message; }
        finally { _benchRunning = false; }
    }
}
