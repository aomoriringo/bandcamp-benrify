using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BandcampBenrify.Core;
using BandcampBenrify.Infrastructure;
using BandcampBenrify.ViewModels;

namespace BandcampBenrify;

public partial class MainWindow : Window
{
    // 数値の列は最初のクリックで「多い順 / 新しい順」にする
    private static readonly HashSet<string> DescendingFirst = ["TotalSort", "AlbumSupSort", "TrackSupSort", "SupSort", "ReleaseDate", "TrackCount"];

    /// <summary>初回 (保存された位置が無いとき) のウィンドウの大きさ: 作業領域に対する割合。</summary>
    private const double InitialSizeRatio = 0.7;

    public MainWindow()
    {
        InitializeComponent();

        // 波形 (シークバー) を押したとき。つまみ以外を押すと Slider が自分で処理済みにするので、処理済みでも受け取る
        SeekBar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SeekBar_MouseDown), handledEventsToo: true);

        // 「探す」の追加ボタン: 押すと入れるキューを選ぶメニューを出す (使えるかどうかは元の追加のコマンドに合わせる)
        AddReleaseButton.Command = new RelayCommand(
            () => OpenQueuePicker(AddReleaseButton, "releases", Vm?.Browse.SelectedItem is { } release ? [release] : []),
            () => Vm?.Browse.AddCurrentReleaseCommand.CanExecute(null) == true);
        AddTracksButton.Command = new RelayCommand(
            () => OpenQueuePicker(AddTracksButton, "tracks", TrackGrid.SelectedItems.Cast<object>().ToList()),
            () => TrackGrid.SelectedItems.Count > 0);

        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm)
            {
                ApplySavedBounds(vm.Settings.Window);
                vm.VisualizerRequested += OpenVisualizer;
                vm.PropertyChanged += (_, args) =>
                {
                    // 再生中の曲が見えるようにスクロールする
                    if (args.PropertyName == nameof(MainViewModel.Current) && vm.Current is { } current)
                    {
                        QueueGrid.ScrollIntoView(current);
                    }
                };
            }
        };
        Closing += (_, _) =>
        {
            // ビジュアライザも閉じる (位置の保存を先に済ませる)
            _visualizer?.Close();

            if (Vm is { } vm)
            {
                var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                vm.Settings.Window = new WindowBounds(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized, WindowBounds.CurrentVersion);
            }

            Theme.Changed -= UpdatePlayerColors;
        };

        // 再生バーの色: 表示したジャケット画像から色を取る (色のために画像を取り直すことはしない)
        DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(NowArtImage, (_, _) => OnNowArtChanged());
        Theme.Changed += UpdatePlayerColors;
    }

    /// <summary>再生中のジャケットの代表色 (色味のないジャケットや未表示なら null)。</summary>
    private Rgb? _artColor;

    private void OnNowArtChanged()
    {
        _artColor = null;
        if (NowArtImage.Source is BitmapSource bitmap)
        {
            if (bitmap.IsDownloading)
            {
                bitmap.DownloadCompleted += (_, _) =>
                {
                    if (ReferenceEquals(NowArtImage.Source, bitmap))
                    {
                        _artColor = PickArtColor(bitmap);
                        UpdatePlayerColors();
                    }
                };
            }
            else
            {
                _artColor = PickArtColor(bitmap);
            }
        }

        UpdatePlayerColors();
    }

    private static Rgb? PickArtColor(BitmapSource bitmap) => ArtColors.Pick(bitmap);

    private VisualizerWindow? _visualizer;

    /// <summary>ビジュアライザを開く (開いていれば前に出す)。</summary>
    private void OpenVisualizer()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (_visualizer is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        _visualizer = new VisualizerWindow(vm);
        _visualizer.Closed += (_, _) => _visualizer = null;
        _visualizer.Show();
    }

    private void NowArt_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenVisualizer();

    /// <summary>再生バーの背景とアクセントを、ジャケットの色 (設定でオフならテーマカラー) にする。</summary>
    private void UpdatePlayerColors()
    {
        if (Theme.PlayerUsesArtColor && _artColor is { } c)
        {
            var accent = ArtPalette.Accent(c);
            var bg = ArtPalette.Background(c);
            Theme.SetAccent(PlayerBar.Resources, Color.FromRgb(accent.R, accent.G, accent.B));
            PlayerBar.Background = Theme.Frozen(Color.FromRgb(bg.R, bg.G, bg.B));
        }
        else
        {
            Theme.ClearAccent(PlayerBar.Resources);
            PlayerBar.Background = (Brush)FindResource("SurfaceBrush");
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>前回の位置とサイズ。初回は画面 (作業領域) の 7 割の大きさで中央に置く。</summary>
    private void ApplySavedBounds(WindowBounds? saved)
    {
        var area = SystemParameters.WorkArea;
        if (saved is not null && saved.Version == WindowBounds.CurrentVersion && saved.Width >= MinWidth && saved.Height >= MinHeight
            && saved.Left < area.Right - 100 && saved.Top < area.Bottom - 100 && saved.Left + saved.Width > area.Left + 100 && saved.Top >= area.Top - 20)
        {
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
            if (saved.Maximized)
            {
                WindowState = WindowState.Maximized;
            }

            return;
        }

        Width = Math.Max(MinWidth, area.Width * InitialSizeRatio);
        Height = Math.Max(MinHeight, area.Height * InitialSizeRatio);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (sender is DataGrid grid && e.Column.SortDirection is null && DescendingFirst.Contains(e.Column.SortMemberPath))
        {
            e.Handled = true;
            var view = grid.Items;
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, ListSortDirection.Descending));
            foreach (var c in grid.Columns)
            {
                c.SortDirection = null;
            }

            e.Column.SortDirection = ListSortDirection.Descending;
        }
    }

    /// <summary>作品一覧: アーティスト / レーベルの行をダブルクリックしたらその作品一覧を開く。</summary>
    private void ReleaseGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext is BrowseItemViewModel { Kind: Core.SearchKind.Band } item
            && Vm is { } vm)
        {
            vm.InputUrl = item.Url;
            vm.Browse.OpenItemCommand.Execute(item);
        }
    }

    /// <summary>曲目: ダブルクリックで試聴 (キューには入れない)。</summary>
    private void TrackGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext is QueueItemViewModel item && Vm is { } vm)
        {
            vm.Browse.PreviewCommand.Execute(item);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            new SettingsWindow(vm) { Owner = this }.ShowDialog();
        }
    }

    // ---- シークバー (波形) ----
    // 押してから離すまでは再生位置の定期更新を止める (IsSeeking)。止めないと、押している間に更新が入ったとき
    // クリックした位置が今の再生位置に書き戻され、離したときに元の位置へシークしてしまう。

    private bool _seekBarPressed;
    private bool _seekThumbDragging;

    private void SeekBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { HasCurrent: true } vm)
        {
            return;
        }

        _seekBarPressed = true;
        vm.IsSeeking = true;

        // つまみ以外を押したとき: 離したときにシークバーの外でも受け取れるようにマウスをつかまえておく
        // (つまみはドラッグのために自分でつかまえる)
        if (!IsOnThumb(e.OriginalSource as DependencyObject))
        {
            SeekBar.CaptureMouse();
        }
    }

    private void SeekBar_DragStarted(object sender, DragStartedEventArgs e)
    {
        _seekThumbDragging = true;
        if (Vm is { } vm)
        {
            vm.IsSeeking = true;
        }
    }

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _seekThumbDragging = false;
        FinishSeek(seek: true);
    }

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // つまみのドラッグは DragCompleted で処理する
        if (!_seekThumbDragging)
        {
            FinishSeek(seek: true);
        }
    }

    private void SeekBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // 離す前にマウスを奪われた (別のウィンドウに切り替えたなど) ときは、シークせずに定期更新を戻す
        if (!_seekThumbDragging && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            FinishSeek(seek: false);
        }
    }

    private void FinishSeek(bool seek)
    {
        if (!_seekBarPressed)
        {
            return; // 押していない (別の場所で押して、ここで離したなど)
        }

        _seekBarPressed = false;
        if (SeekBar.IsMouseCaptured)
        {
            SeekBar.ReleaseMouseCapture();
        }

        if (Vm is { } vm)
        {
            if (seek)
            {
                vm.SeekTo(SeekBar.Value);
            }

            vm.IsSeeking = false;
        }
    }

    private bool IsOnThumb(DependencyObject? element)
    {
        for (var e = element; e is not null && e != SeekBar; e = VisualTreeHelper.GetParent(e))
        {
            if (e is Thumb)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Delete キーで削除。DataGrid は Delete を自前の行削除コマンドで先に処理してしまう (読み取り専用だと何もしない) ので、
    /// InputBindings ではなくここで拾う。
    /// </summary>
    private void QueueGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Delete)
        {
            e.Handled = Run(vm.RemoveSelectedCommand, QueueGrid.SelectedItems);
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = Run(vm.PlayItemCommand, QueueGrid.SelectedItem);
        }
    }

    /// <summary>探す: 曲目で Enter = 試聴、Ctrl+Enter = キューに追加。</summary>
    private void TrackGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = Keyboard.Modifiers == ModifierKeys.Control
            ? Run(vm.Browse.AddTracksCommand, TrackGrid.SelectedItems)
            : Run(vm.Browse.PreviewCommand, TrackGrid.SelectedItem);
    }

    /// <summary>探す: 作品一覧で Ctrl+Enter = 選んだ作品の全曲をキューに追加。</summary>
    private void ReleaseGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is { } vm && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = Run(vm.Browse.AddReleasesCommand, ReleaseGrid.SelectedItems);
        }
    }

    private static bool Run(ICommand command, object? parameter)
    {
        if (!command.CanExecute(parameter))
        {
            return false;
        }

        command.Execute(parameter);
        return true;
    }

    private void QueueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext is QueueItemViewModel item
            && Vm?.PlayItemCommand.CanExecute(item) == true)
        {
            Vm.PlayItemCommand.Execute(item);
        }
    }

    /// <summary>キューの切り替え欄: ダブルクリックで名前を変更。</summary>
    private void QueueTab_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: QueueListItemViewModel queue } && Vm is { } vm)
        {
            e.Handled = Run(vm.RenameQueueCommand, queue);
        }
    }

    /// <summary>
    /// 右クリックの「キューに追加」系のサブメニュー: 開くたびに、今あるキューの一覧と「新しいキューを作って…」を並べる。
    /// Tag: tracks (探す の曲目) / releases (探す の作品一覧) / copy・move (キューの曲を別のキューへ)。
    /// </summary>
    private void AddToQueueMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || !ReferenceEquals(e.OriginalSource, menu) || Vm is not { } vm)
        {
            return;
        }

        DependencyObject? d = menu;
        while (d is not null and not System.Windows.Controls.ContextMenu)
        {
            d = LogicalTreeHelper.GetParent(d);
        }

        var selected = ((d as System.Windows.Controls.ContextMenu)?.PlacementTarget as DataGrid)?.SelectedItems.Cast<object>().ToList() ?? [];
        FillQueueMenu(menu, vm, menu.Tag as string, selected);
    }

    /// <summary>
    /// 「探す」の「この作品の全曲をキューに追加」「選択した曲をキューに追加」: 押すと、入れるキューを選ぶメニューを出す。
    /// </summary>
    private void OpenQueuePicker(Button button, string kind, List<object> selected)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        FillQueueMenu(menu, vm, kind, selected);
        menu.IsOpen = true;
    }

    /// <summary>キューの一覧と「新しいキューを作って…」を並べる。選ぶと <paramref name="selected"/> をそのキューに入れる。</summary>
    private static void FillQueueMenu(ItemsControl menu, MainViewModel vm, string? kind, List<object> selected)
    {
        var toOther = kind is "copy" or "move";
        menu.Items.Clear();
        foreach (var queue in vm.Queues)
        {
            if (toOther && ReferenceEquals(queue, vm.SelectedQueue))
            {
                continue; // 表示しているキュー自身には入れない
            }

            var label = $"{queue.Name}  ({queue.Count})";
            var item = new MenuItem { Header = new TextBlock { Text = label }, IsEnabled = selected.Count > 0 };
            var saved = queue.Saved;
            item.Click += (_, _) => AddSelectionTo(vm, kind, selected, saved);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var create = new MenuItem
        {
            Header = new TextBlock { Text = kind == "move" ? "新しいキューを作って移動…" : "新しいキューを作って追加…" },
            IsEnabled = selected.Count > 0,
        };
        create.Click += (_, _) =>
        {
            if (vm.CreateQueue() is { } saved)
            {
                AddSelectionTo(vm, kind, selected, saved);
            }
        };
        menu.Items.Add(create);
    }

    private static void AddSelectionTo(MainViewModel vm, string? kind, List<object> selected, SavedQueue target)
    {
        switch (kind)
        {
            case "tracks":
                vm.Browse.AddTracksTo(selected.OfType<QueueItemViewModel>().ToList(), target);
                break;
            case "releases":
                if (selected.OfType<BrowseItemViewModel>().Where(r => r.IsRelease).ToList() is { Count: > 0 } releases)
                {
                    _ = MainViewModel.RunSafeAsync(vm.Browse.AddReleasesAsync(releases, target));
                }

                break;
            case "copy":
            case "move":
                vm.CopyToQueue(selected.OfType<QueueItemViewModel>().ToList(), target, move: kind == "move");
                break;
        }
    }

    /// <summary>
    /// キューの名前の並びのすぐ右に「新しいキュー」を置く。並びが長くなったら、ボタンの分を残して折り返す。
    /// </summary>
    private void QueueBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var button = NewQueueButton.ActualWidth > 0 ? NewQueueButton.ActualWidth : 160;
        QueueTabs.MaxWidth = Math.Max(0, QueueBar.ActualWidth - button - 8);
    }

    /// <summary>キューの切り替え欄: 右クリックはメニューを出すだけで、キューは切り替えない。</summary>
    private void QueueTab_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private static T? FindAncestor<T>(DependencyObject? d)
        where T : DependencyObject
    {
        while (d is not null and not T)
        {
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }

        return d as T;
    }
}
