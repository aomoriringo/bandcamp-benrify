using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BandcampBenrify.ViewModels;

namespace BandcampBenrify;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm)
            {
                vm.PropertyChanged += (_, args) =>
                {
                    // 再生中の曲が見えるようにスクロールする
                    if (args.PropertyName == nameof(MainViewModel.Current) && vm.Current is { } current)
                    {
                        QueueList.ScrollIntoView(current);
                    }
                };
            }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void SeekBar_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.IsSeeking = true;
        }
    }

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.SeekTo(SeekBar.Value);
            vm.IsSeeking = false;
        }
    }

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // つまみのドラッグ以外 (バーのクリック) でのシーク
        if (Vm is { IsSeeking: false } vm)
        {
            vm.SeekTo(SeekBar.Value);
        }
    }

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is QueueItemViewModel item
            && Vm?.PlayItemCommand.CanExecute(item) == true)
        {
            Vm.PlayItemCommand.Execute(item);
        }
    }

    private void RankingGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext is RankingRowViewModel row
            && Vm?.Ranking.AddSelectedCommand is { } cmd)
        {
            var param = new List<RankingRowViewModel> { row };
            if (cmd.CanExecute(param))
            {
                cmd.Execute(param);
            }
        }
    }

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
