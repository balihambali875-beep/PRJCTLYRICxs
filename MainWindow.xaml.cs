using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LyricPop;

/// <summary>Animasi scroll halus: ScrollViewer tidak punya DependencyProperty offset, jadi dibuat sendiri.</summary>
public static class ScrollAnim
{
    public static readonly DependencyProperty VOffsetProperty = DependencyProperty.RegisterAttached(
        "VOffset", typeof(double), typeof(ScrollAnim),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    public static void SetVOffset(DependencyObject o, double v) => o.SetValue(VOffsetProperty, v);
    public static double GetVOffset(DependencyObject o) => (double)o.GetValue(VOffsetProperty);
}

/// <summary>
/// Widget lirik mengambang sesuai konsep "Nocturnal Lyrics Widget":
/// compact (pill) saat mouse tidak diarahkan, expanded (kartu lengkap) saat di-hover.
/// Sumber lagu: Windows media session (MediaWatcher). Lirik: LRCLIB (LyricsClient).
/// </summary>
public partial class MainWindow : Window
{
    private enum LStatus { Idle, Loading, Synced, Plain, Instrumental, None, Error }

    private const double CompactW = 340, ExpandedW = 600, Pad = 20; // Pad = margin ruang glow
    private const double Lead = 0.2; // tampilkan baris sedikit lebih awal (detik)
    private static readonly TimeSpan IdleHide = TimeSpan.FromSeconds(30);

    private const string GlyphPlay = "\uE768", GlyphPause = "\uE769", GlyphRepeatAll = "\uE8EE", GlyphRepeatOne = "\uE8ED";

    private readonly AppSettings _s = AppSettings.Load();
    private readonly MediaWatcher _watcher = new();
    private readonly LyricsClient _client = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _hoverTimer;

    private bool _expanded, _pinned, _menuOpen, _shown;
    private int _topTick;

    private LStatus _status = LStatus.Idle;
    private List<LrcLine> _lines = new();
    private List<string> _plain = new();
    private LyricData? _data;
    private readonly List<TextBlock> _lineViews = new();
    private int _activeIdx = -2;

    private string? _trackKey, _dismissedKey;
    private Track? _track;
    private bool _everPlayed;
    private DateTime? _pausedSince;
    private Hue? _palette;
    private int _lastThumbGen = -1, _lyricToken;
    private DateTime _userScrollUntil = DateTime.MinValue;
    private bool? _lastPlaying;

    private Brush _fg = Brushes.White, _dim = Brushes.Gray, _faint = Brushes.Gray, _accent = Brushes.HotPink;
    private readonly List<(Rectangle bar, double dur, double min, double max)> _waveBars = new();

    private readonly Dictionary<string, MenuItem> _themeItems = new();
    private readonly Dictionary<int, MenuItem> _opacityItems = new();
    private MenuItem? _pinItem, _onTopItem, _autoItem, _offsetResetItem;

    public MainWindow()
    {
        InitializeComponent();
        Topmost = _s.AlwaysOnTop;
        SourceInitialized += (_, _) => Native.MakeFloating(this); // tidak mencuri fokus, tidak di Alt+Tab

        BuildWave();
        Wire();
        BuildMenu();
        ApplyStyle();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => { try { Frame(); } catch (Exception) { /* jaga agar timer tidak berhenti */ } };

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (!Root.IsMouseOver && !_pinned && !_menuOpen) SetExpanded(false);
        };
    }

    public async Task StartAsync()
    {
        try { await _watcher.InitAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("Tidak bisa membaca media session Windows.\nButuh Windows 10 (1809) atau lebih baru.\n\n" + ex.Message,
                "Lyric Pop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _timer.Start();
    }

    public void Cleanup() { _timer.Stop(); _hoverTimer.Stop(); }

    /// <summary>Dipanggil dari tray: tampilkan lagi widget yang ditutup / disembunyikan.</summary>
    public void ShowAgain()
    {
        _dismissedKey = null;
        _everPlayed = true;
        if (!_watcher.Playing) _pausedSince = DateTime.UtcNow;
    }

    public static void ShowAbout() =>
        MessageBox.Show("Lyric Pop 1.0\nWidget lirik tersinkron untuk Windows.\n\nDibuat oleh arlnxs\nLirik dari LRCLIB (lrclib.net)",
            "Tentang Lyric Pop", MessageBoxButton.OK, MessageBoxImage.Information);

    // ================================================================ input
    private void Wire()
    {
        BtnPlay.Click += async (_, _) => await _watcher.TogglePlayPauseAsync();
        BtnPrev.Click += async (_, _) => await _watcher.PrevAsync();
        BtnNext.Click += async (_, _) => await _watcher.NextAsync();
        BtnShuffle.Click += async (_, _) => await _watcher.ToggleShuffleAsync();
        BtnRepeat.Click += async (_, _) => await _watcher.CycleRepeatAsync();
        BtnClose.Click += (_, _) => { _dismissedKey = _trackKey; };
        BtnGear.Click += (_, _) =>
        {
            var m = Root.ContextMenu;
            m.PlacementTarget = BtnGear;
            m.Placement = PlacementMode.Bottom;
            m.IsOpen = true;
        };

        ManualLink.MouseLeftButtonUp += (_, _) => OpenSearch();
        Bar.MouseLeftButtonDown += OnBarDown;
        Scroll.PreviewMouseWheel += (_, _) => _userScrollUntil = DateTime.UtcNow.AddSeconds(3.5);

        // Hover: arahkan mouse -> kartu lengkap; jauhkan -> kembali ke pill
        Root.MouseEnter += (_, _) => { _hoverTimer.Stop(); SetExpanded(true); };
        Root.MouseLeave += (_, _) => { _hoverTimer.Stop(); _hoverTimer.Start(); };
        Root.PreviewMouseLeftButtonDown += OnRootDown;
    }

    // Seret di area kosong = pindahkan window. Klik tanpa geser = sematkan (pin) tampilan lengkap.
    private void OnRootDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject src &&
            (HasAncestor<ButtonBase>(src) || IsInside(src, Bar) || IsInside(src, Scroll) || IsInside(src, ManualLink)))
            return;

        double l0 = Left, t0 = Top;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        if (Math.Abs(Left - l0) < 3 && Math.Abs(Top - t0) < 3)
        {
            _pinned = !_pinned;
            SetExpanded(_pinned || Root.IsMouseOver);
        }
        else
        {
            _s.CenterX = Left + ActualWidth / 2; _s.Top = Top; _s.Save();
        }
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static bool IsInside(DependencyObject? d, DependencyObject target)
    {
        while (d != null) { if (ReferenceEquals(d, target)) return true; d = ParentOf(d); }
        return false;
    }

    private static bool HasAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null) { if (d is T) return true; d = ParentOf(d); }
        return false;
    }

    private async void OnBarDown(object sender, MouseButtonEventArgs e)
    {
        var dur = _watcher.Duration;
        if (dur <= TimeSpan.Zero || !_watcher.CanSeek || Bar.ActualWidth <= 0) return;
        double r = Math.Clamp(e.GetPosition(Bar).X / Bar.ActualWidth, 0, 1);
        await _watcher.SeekAsync(TimeSpan.FromSeconds(dur.TotalSeconds * r));
    }

    // ============================================================= menu (klik kanan / gear)
    private void BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string header, Action act)
        {
            var i = new MenuItem { Header = header };
            i.Click += (_, _) => act();
            return i;
        }

        var theme = new MenuItem { Header = "Tema" };
        foreach (var (key, name) in new[] { ("dark", "Gelap (neon)"), ("light", "Terang"), ("artwork", "Warna artwork") })
        {
            var it = Item(name, () => { _s.Theme = key; _s.Save(); ApplyStyle(); });
            _themeItems[key] = it;
            theme.Items.Add(it);
        }
        m.Items.Add(theme);

        var font = new MenuItem { Header = "Ukuran font lirik" };
        font.Items.Add(Item("Lebih besar (A+)", () => SetFont(_s.FontSize + 1)));
        font.Items.Add(Item("Lebih kecil (A\u2212)", () => SetFont(_s.FontSize - 1)));
        m.Items.Add(font);

        var op = new MenuItem { Header = "Transparansi latar" };
        foreach (var v in new[] { 100, 90, 80, 70, 60, 50 })
        {
            int val = v;
            var it = Item(val + "%", () => { _s.Opacity = val; _s.Save(); ApplyStyle(); });
            _opacityItems[val] = it;
            op.Items.Add(it);
        }
        m.Items.Add(op);

        var sync = new MenuItem { Header = "Sinkron lirik" };
        sync.Items.Add(Item("Lirik lebih awal (+0,5 dtk)", () => SetOffset(_s.OffsetSec + 0.5)));
        sync.Items.Add(Item("Lirik lebih lambat (\u22120,5 dtk)", () => SetOffset(_s.OffsetSec - 0.5)));
        _offsetResetItem = Item("Reset", () => SetOffset(0));
        sync.Items.Add(_offsetResetItem);
        m.Items.Add(sync);

        m.Items.Add(new Separator());
        m.Items.Add(Item("Cari lirik manual\u2026", OpenSearch));
        _pinItem = Item("Tetap tampil lengkap", () => { _pinned = !_pinned; SetExpanded(_pinned || Root.IsMouseOver); });
        m.Items.Add(_pinItem);
        _onTopItem = Item("Selalu di atas", () => { _s.AlwaysOnTop = !_s.AlwaysOnTop; Topmost = _s.AlwaysOnTop; _s.Save(); });
        m.Items.Add(_onTopItem);
        _autoItem = Item("Mulai bersama Windows", () => Autostart.Set(!Autostart.IsEnabled()));
        m.Items.Add(_autoItem);
        m.Items.Add(Item("Reset posisi", () =>
        {
            _s.CenterX = null; _s.Top = null; _s.Save();
            PositionWindow(true);
        }));
        m.Items.Add(Item("Hapus cache lirik", () =>
        {
            int n = _client.ClearCache();
            MessageBox.Show($"Cache dihapus ({n} lagu).", "Lyric Pop", MessageBoxButton.OK, MessageBoxImage.Information);
        }));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Tentang \u2014 dibuat oleh arlnxs", ShowAbout));
        m.Items.Add(Item("Keluar", () => Application.Current.Shutdown()));

        m.Opened += (_, _) =>
        {
            _menuOpen = true;
            foreach (var kv in _themeItems) kv.Value.IsChecked = _s.Theme == kv.Key;
            foreach (var kv in _opacityItems) kv.Value.IsChecked = _s.Opacity == kv.Key;
            if (_pinItem != null) _pinItem.IsChecked = _pinned;
            if (_onTopItem != null) _onTopItem.IsChecked = _s.AlwaysOnTop;
            if (_autoItem != null) _autoItem.IsChecked = Autostart.IsEnabled();
            if (_offsetResetItem != null) _offsetResetItem.Header = $"Reset (sekarang {_s.OffsetSec:+0.0;-0.0;0} dtk)";
        };
        m.Closed += (_, _) =>
        {
            _menuOpen = false;
            m.Placement = PlacementMode.MousePoint; // kembalikan untuk klik kanan berikutnya
            m.PlacementTarget = null;
            _hoverTimer.Stop(); _hoverTimer.Start();
        };
        Root.ContextMenu = m;
    }

    private void SetFont(int v) { _s.FontSize = Math.Clamp(v, 12, 28); _s.Save(); ApplyStyle(); }
    private void SetOffset(double v) { _s.OffsetSec = Math.Round(Math.Clamp(v, -10, 10), 1); _s.Save(); }

    // ============================================================== tampil / sembunyi / ukuran
    private double CurrentCardWidth => _expanded ? ExpandedW : CompactW;

    private void PositionWindow(bool forceDefault)
    {
        var wa = SystemParameters.WorkArea;
        double w = CurrentCardWidth + 2 * Pad;
        if (!forceDefault && _s.CenterX is double cx && _s.Top is double t) { Left = cx - w / 2; Top = t; }
        else { Left = wa.Left + (wa.Width - w) / 2; Top = wa.Top; }
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);
    }

    private void ClampToScreen()
    {
        double sl = SystemParameters.VirtualScreenLeft, st = SystemParameters.VirtualScreenTop;
        double sw = SystemParameters.VirtualScreenWidth, sh = SystemParameters.VirtualScreenHeight;
        double w = ActualWidth > 0 ? ActualWidth : CurrentCardWidth + 2 * Pad;
        double h = ActualHeight > 0 ? ActualHeight : 100;
        double minL = sl - Pad + 6, maxL = sl + sw - w + Pad - 6;
        double minT = st - Pad + 6, maxT = st + sh - h + Pad - 6;
        Left = Math.Clamp(Left, minL, Math.Max(minL, maxL));
        Top = Math.Clamp(Top, minT, Math.Max(minT, maxT));
    }

    /// <summary>Compact (pill) &lt;-> expanded (kartu lengkap); tetap berpusat di titik yang sama.</summary>
    private void SetExpanded(bool on)
    {
        if (_expanded == on) return;
        double oldW = CurrentCardWidth;
        _expanded = on;
        double newW = CurrentCardWidth;

        CompactView.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ExpandedView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Root.Width = newW;
        Root.CornerRadius = new CornerRadius(on ? 26 : 32);
        Left -= (newW - oldW) / 2;
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);

        var d = TimeSpan.FromMilliseconds(180);
        Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.5, 1, d));
        var scale = new DoubleAnimation(0.96, 1, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        if (on) Dispatcher.BeginInvoke(new Action(() => ScrollToActive(true)), DispatcherPriority.Loaded);
    }

    private void ShowWidget()
    {
        _shown = true;
        if (!IsVisible) { PositionWindow(false); Show(); }
        Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(440))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void HideWidget()
    {
        _shown = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => { if (!_shown) Hide(); };
        Root.BeginAnimation(UIElement.OpacityProperty, fade);
        RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-18, TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>Animasi ganti lagu: keluar (fade) -> ganti isi -> masuk.</summary>
    private void Swap(Action apply)
    {
        var o = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        o.Completed += (_, _) =>
        {
            apply();
            Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        };
        Root.BeginAnimation(UIElement.OpacityProperty, o);
    }

    private bool ShouldShow()
    {
        if (!_watcher.HasMeta || _track == null) return false;
        if (_dismissedKey != null && _dismissedKey == _trackKey) return false;
        if (_watcher.Playing) return true;
        return _everPlayed && _pausedSince != null && DateTime.UtcNow - _pausedSince.Value < IdleHide;
    }

    // ================================================================== loop utama
    private void Frame()
    {
        _watcher.Poll();
        Reconcile();

        bool show = ShouldShow();
        if (show && !_shown) ShowWidget();
        else if (!show && _shown) HideWidget();
        if (!_shown) return;

        // Tegaskan "paling atas" berkala (~2 dtk) agar tetap melayang di atas jendela lain
        if (_s.AlwaysOnTop && ++_topTick % 16 == 0) Native.KeepOnTop(this);

        var pos = _watcher.Position();
        UpdateProgress(pos);
        if (_status == LStatus.Synced)
        {
            int idx = Txt.IndexAt(_lines, pos.TotalSeconds + _s.OffsetSec + Lead);
            if (idx != _activeIdx) { _activeIdx = idx; Highlight(idx); }
        }
    }

    private void Reconcile()
    {
        if (!_watcher.HasMeta) { _pausedSince = null; return; }

        var key = _watcher.Title + "\u241f" + _watcher.Artist + "\u241f" + _watcher.Album;
        if (key != _trackKey) ChangeTrack(key);
        else if (_watcher.ThumbGen != _lastThumbGen) ApplyArt();

        if (_watcher.Playing) { _everPlayed = true; _pausedSince = null; }
        else _pausedSince ??= DateTime.UtcNow;

        if (_lastPlaying != _watcher.Playing)
        {
            _lastPlaying = _watcher.Playing;
            PlayIcon.Text = _watcher.Playing ? GlyphPause : GlyphPlay;
            SetWave(_watcher.Playing);
        }
        BtnPrev.IsEnabled = _watcher.CanPrev;
        BtnNext.IsEnabled = _watcher.CanNext;
        BtnShuffle.IsEnabled = _watcher.CanShuffle;
        BtnRepeat.IsEnabled = _watcher.CanRepeat;
        ShuffleIcon.Foreground = _watcher.Shuffle ? _accent : _dim;
        RepeatIcon.Foreground = _watcher.Repeat == 0 ? _dim : _accent;
        RepeatIcon.Text = _watcher.Repeat == 2 ? GlyphRepeatOne : GlyphRepeatAll;
    }

    private void ChangeTrack(string key)
    {
        _trackKey = key;
        var t = Txt.ParseTrack(_watcher.Title, _watcher.Artist, _watcher.Album);
        _track = t;
        if (_dismissedKey != null && _dismissedKey != key) _dismissedKey = null;
        _status = LStatus.Loading;
        _lines = new(); _plain = new(); _data = null; _activeIdx = -2;

        Action apply = () =>
        {
            TitleSmall.Text = t.Title; TitleBig.Text = t.Title;
            string a = t.Artist.ToUpperInvariant();
            ArtistSmall.Text = a; ArtistBig.Text = a;
            RenderLyrics();
            ApplyArt();
        };
        if (_shown) Swap(apply); else apply();

        int token = ++_lyricToken;
        _ = FetchLyricsAsync(token, t);
    }

    private async Task FetchLyricsAsync(int token, Track t)
    {
        await Task.Delay(500); // debounce: metadata sering berubah beruntun
        if (token != _lyricToken) return;
        int dur = (int)_watcher.Duration.TotalSeconds;
        var (ok, d) = await _client.GetAsync(t, dur);
        if (token != _lyricToken) return;
        ApplyResult(ok, d);
    }

    private void ApplyResult(bool ok, LyricData? d)
    {
        _activeIdx = -2; _lines = new(); _plain = new(); _data = null;
        if (!ok) _status = LStatus.Error;
        else if (d == null) _status = LStatus.None;
        else
        {
            var parsed = d.Synced.Length > 0 ? Txt.ParseLrc(d.Synced) : new List<LrcLine>();
            if (parsed.Count > 0) { _status = LStatus.Synced; _lines = parsed; _data = d; }
            else if (d.Plain.Length > 0) { _status = LStatus.Plain; _plain = Txt.SplitLines(d.Plain).ToList(); _data = d; }
            else if (d.IsInstrumental) { _status = LStatus.Instrumental; _data = d; }
            else _status = LStatus.None;
        }
        RenderLyrics();
    }

    private string SrcLabel()
    {
        string s = _status switch
        {
            LStatus.Synced => "LRCLIB \u00B7 tersinkron \u00B7 ",
            LStatus.Plain => "LRCLIB \u00B7 teks biasa \u00B7 ",
            _ => ""
        };
        return s + "dibuat oleh arlnxs";
    }

    private void OpenSearch()
    {
        var t = _track;
        string q = t == null ? "" : (t.Title + " " + (t.Primary.Length > 0 ? t.Primary : t.Artist)).Trim();
        var w = new SearchWindow(_client, q, item =>
        {
            if (_track == null) return;
            _lyricToken++; // batalkan pencarian otomatis yang masih berjalan
            _client.Save(_track, item);
            ApplyResult(true, item);
        });
        w.Show();
        w.Activate();
    }

    // ================================================================== waveform (compact)
    private void BuildWave()
    {
        var spec = new (double dur, double min, double max)[]
        {
            (0.55, 8, 26), (0.42, 6, 20), (0.70, 10, 30), (0.48, 7, 24), (0.62, 9, 28), (0.38, 6, 18), (0.58, 8, 25)
        };
        foreach (var (dur, min, max) in spec)
        {
            var r = new Rectangle
            {
                Width = 3, Height = min, RadiusX = 1.5, RadiusY = 1.5,
                Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center
            };
            Wave.Children.Add(r);
            _waveBars.Add((r, dur, min, max));
        }
    }

    private void SetWave(bool playing)
    {
        foreach (var (bar, dur, min, max) in _waveBars)
        {
            if (playing)
            {
                bar.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(min, max, TimeSpan.FromSeconds(dur))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase()
                });
            }
            else
            {
                bar.BeginAnimation(FrameworkElement.HeightProperty, null);
                bar.Height = min;
            }
        }
    }

    // ================================================================== artwork
    private void ApplyArt()
    {
        _lastThumbGen = _watcher.ThumbGen;
        var bytes = _watcher.Thumb;
        _palette = null;
        Brush art;
        if (bytes == null || bytes.Length == 0)
        {
            art = new LinearGradientBrush(Color.FromRgb(0xFF, 0x6A, 0xA2), Color.FromRgb(0x7C, 0x5C, 0xFF), 45);
            ArtNote.Visibility = Visibility.Visible;
        }
        else
        {
            try
            {
                var bmp = new BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 256;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                art = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                ArtNote.Visibility = Visibility.Collapsed;
                _palette = Txt.DominantHue(SamplePixels(bmp));
            }
            catch (Exception)
            {
                art = new LinearGradientBrush(Color.FromRgb(0xFF, 0x6A, 0xA2), Color.FromRgb(0x7C, 0x5C, 0xFF), 45);
                ArtNote.Visibility = Visibility.Visible;
            }
        }
        ArtSmall.Background = art;
        ArtBig.Background = art;
        ApplyStyle();
    }

    private static byte[] SamplePixels(BitmapSource bmp)
    {
        var scaled = new TransformedBitmap(bmp, new ScaleTransform(32.0 / bmp.PixelWidth, 32.0 / bmp.PixelHeight));
        var fmt = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        int w = fmt.PixelWidth, h = fmt.PixelHeight;
        var px = new byte[w * h * 4];
        fmt.CopyPixels(px, w * 4, 0);
        return px;
    }

    // ================================================================== tema (neon ungu-pink)
    private void ApplyStyle()
    {
        bool light = _s.Theme == "light";
        // "dark" memakai nuansa ungu-magenta tetap seperti konsep; "artwork" mengikuti warna cover.
        var h = _s.Theme == "artwork" ? (_palette ?? new Hue(285, 0.8)) : new Hue(285, 0.8);
        var fgC = light ? Color.FromRgb(0x1A, 0x16, 0x2A) : Color.FromRgb(0xF4, 0xF1, 0xFF);
        _fg = Pal.Brush(fgC);
        _dim = Pal.Brush(Pal.WithAlpha(fgC, 0.62));
        _faint = Pal.Brush(Pal.WithAlpha(fgC, 0.40));

        double sat = h.S < 0.12 ? h.S : Math.Clamp(h.S, 0.6, 0.95);
        var accentC = Pal.Hsl(h.H, sat, light ? 0.46 : 0.70);
        _accent = Pal.Brush(accentC);

        double a = Math.Clamp(_s.Opacity, 30, 100) / 100.0;
        double s2 = Math.Clamp(h.S * 0.75, 0.3, 0.65);
        if (light)
        {
            Root.Background = new LinearGradientBrush(
                Pal.WithAlpha(Color.FromRgb(0xFF, 0xFF, 0xFF), a), Pal.WithAlpha(Color.FromRgb(0xF1, 0xEC, 0xFA), a), 45);
        }
        else
        {
            // Latar gelap kebiruan dengan cahaya ungu/pink di sudut kanan-bawah (seperti konsep)
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop(Pal.WithAlpha(Pal.Hsl(h.H - 20, s2, 0.075), a), 0));
            g.GradientStops.Add(new GradientStop(Pal.WithAlpha(Pal.Hsl(h.H, s2, 0.12), a), 0.62));
            g.GradientStops.Add(new GradientStop(Pal.WithAlpha(Pal.Hsl(h.H + 38, Math.Min(0.9, s2 + 0.2), 0.24), a), 1));
            Root.Background = g;
        }

        // Garis tepi gradien neon + glow
        var border = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        border.GradientStops.Add(new GradientStop(Pal.Hsl(h.H - 35, 0.9, 0.62), 0));
        border.GradientStops.Add(new GradientStop(Pal.Hsl(h.H, 0.7, 0.45), 0.5));
        border.GradientStops.Add(new GradientStop(Pal.Hsl(h.H + 45, 0.95, 0.66), 1));
        Root.BorderBrush = border;
        Glow.Color = Pal.Hsl(h.H, 0.9, 0.58);
        Glow.Opacity = light ? 0.28 : 0.55;

        TitleSmall.Foreground = _fg; TitleBig.Foreground = _fg;
        ArtistSmall.Foreground = _dim; ArtistBig.Foreground = _dim;
        LyricSmall.Foreground = _accent;
        LyricSmall.FontSize = Math.Max(10.5, _s.FontSize * 0.7);
        CurText.Foreground = _dim; DurText.Foreground = _dim;
        StateText.Foreground = _dim;
        SrcText.Foreground = _faint;
        ManualLink.Foreground = _dim;
        Divider.Background = Pal.Brush(Pal.WithAlpha(fgC, 0.14));
        BarTrack.Background = Pal.Brush(Pal.WithAlpha(fgC, 0.18));
        BarFill.Background = _accent;
        ActiveBar.Fill = _accent;

        foreach (var b in new[] { BtnPrev, BtnNext, BtnGear, BtnClose }) b.Foreground = _fg;
        BtnGear.Foreground = _dim; BtnClose.Foreground = _dim;
        BtnPlay.Foreground = _fg;
        BtnPlay.BorderBrush = _accent;
        BtnPlay.Background = Pal.Brush(Pal.WithAlpha(accentC, 0.20));
        BtnShuffle.Foreground = _dim; BtnRepeat.Foreground = _dim;
        PlayIcon.Foreground = _fg;

        foreach (var (bar, _, _, _) in _waveBars) bar.Fill = _accent;
        foreach (var tb in Lines.Children.OfType<TextBlock>())
        {
            tb.Foreground = _fg;
            tb.FontSize = _s.FontSize;
        }
    }

    // ================================================================== lirik
    private TextBlock MakeLine(string text, bool plain) => new()
    {
        Text = text,
        FontSize = _s.FontSize,
        FontWeight = plain ? FontWeights.Normal : FontWeights.SemiBold,
        Foreground = _fg,
        TextAlignment = TextAlignment.Left,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 5, 0, 5),
        Opacity = plain ? 0.86 : 0.40,
        RenderTransformOrigin = new Point(0, 0.5), // membesar ke kanan, tepi kiri tetap sejajar dengan garis aksen
        RenderTransform = new ScaleTransform(1, 1)
    };

    private void SetNow(string text)
    {
        if (LyricSmall.Text == text) return;
        LyricSmall.Text = text;
        LyricSmall.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
    }

    private void RenderLyrics()
    {
        Lines.Children.Clear();
        _lineViews.Clear();
        _activeIdx = -2;
        ActiveBar.Visibility = Visibility.Collapsed;
        bool hasList = _status == LStatus.Synced || _status == LStatus.Plain;
        Scroll.Visibility = hasList ? Visibility.Visible : Visibility.Collapsed;
        StateText.Visibility = hasList ? Visibility.Collapsed : Visibility.Visible;
        ManualLink.Visibility = _status == LStatus.Loading || _status == LStatus.Idle ? Visibility.Collapsed : Visibility.Visible;
        ManualLink.Text = _status == LStatus.None || _status == LStatus.Error ? "Cari manual" : "Lirik salah? Cari manual";
        SrcText.Text = SrcLabel();

        switch (_status)
        {
            case LStatus.Synced:
                Lines.Margin = new Thickness(16, 130, 0, 130);
                foreach (var l in _lines)
                {
                    var tb = MakeLine(l.Text.Length > 0 ? l.Text : "\u266A", false);
                    _lineViews.Add(tb);
                    Lines.Children.Add(tb);
                }
                SetNow("\u266A");
                break;
            case LStatus.Plain:
                Lines.Margin = new Thickness(16, 8, 0, 8);
                foreach (var p in _plain) Lines.Children.Add(MakeLine(string.IsNullOrWhiteSpace(p) ? "\u00A0" : p, true));
                SetNow("Lirik tidak tersinkron");
                break;
            case LStatus.Instrumental:
                StateText.Text = "\u266A Instrumental\nLagu ini tidak memiliki lirik.";
                SetNow("\u266A Instrumental");
                break;
            case LStatus.Error:
                StateText.Text = "Gagal memuat lirik.\nPeriksa koneksi internet.";
                SetNow("Gagal memuat lirik");
                break;
            case LStatus.None:
                StateText.Text = "Lirik tidak ditemukan.\nCoba cari dengan judul atau artis lain.";
                SetNow("Lirik tidak ditemukan");
                break;
            default:
                StateText.Text = "Mencari lirik\u2026";
                SetNow("Mencari lirik\u2026");
                break;
        }
    }

    /// <summary>Sorot baris aktif; baris lain redup, yang bersebelahan sedikit lebih terang.</summary>
    private void Highlight(int idx)
    {
        for (int i = _activeIdx - 1; i <= _activeIdx + 1; i++) StyleLine(i, 0);
        _activeIdx = idx;
        StyleLine(idx, 2); StyleLine(idx - 1, 1); StyleLine(idx + 1, 1);
        SetNow(idx >= 0 && idx < _lines.Count ? (_lines[idx].Text.Length > 0 ? _lines[idx].Text : "\u266A") : "\u266A");

        ActiveBar.Visibility = idx >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (idx >= 0 && idx < _lineViews.Count && _lineViews[idx].ActualHeight > 0)
            ActiveBar.Height = _lineViews[idx].ActualHeight * 1.0;
        ScrollToActive(false);
    }

    private void StyleLine(int i, int level)
    {
        if (i < 0 || i >= _lineViews.Count) return;
        var tb = _lineViews[i];
        double o = level == 2 ? 1 : level == 1 ? 0.62 : 0.40;
        double sc = level == 2 ? 1.1 : 1;
        var d = TimeSpan.FromMilliseconds(260);
        tb.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(o, d));
        if (tb.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sc, d));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sc, d));
        }
    }

    private void ScrollToActive(bool instant)
    {
        if (!_expanded || _status != LStatus.Synced) return;
        if (!instant && DateTime.UtcNow < _userScrollUntil) return;
        if (_activeIdx < 0 || _activeIdx >= _lineViews.Count) return;
        var tb = _lineViews[_activeIdx];
        if (!tb.IsLoaded) return;
        double y = Lines.Margin.Top + tb.TranslatePoint(new Point(0, 0), Lines).Y
                   + tb.ActualHeight / 2 - Scroll.ViewportHeight / 2;
        y = Math.Max(0, y);
        if (instant)
        {
            Scroll.BeginAnimation(ScrollAnim.VOffsetProperty, null);
            Scroll.ScrollToVerticalOffset(y);
        }
        else
        {
            Scroll.BeginAnimation(ScrollAnim.VOffsetProperty, new DoubleAnimation(Scroll.VerticalOffset, y, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void UpdateProgress(TimeSpan pos)
    {
        if (!_expanded) return;
        var dur = _watcher.Duration;
        CurText.Text = Txt.Fmt(pos);
        DurText.Text = dur > TimeSpan.Zero ? Txt.Fmt(dur) : "--:--";
        double r = dur > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
        BarFill.Width = Bar.ActualWidth * r;
    }
}
