using Avalonia.Controls;
using XtreamPlayer.App.ViewModels;

namespace XtreamPlayer.App.Views;

public partial class GuidePage : UserControl
{
    public GuidePage()
    {
        InitializeComponent();

        // The ruler and channel column follow the canvas rather than scrolling
        // themselves, which is what keeps them pinned on one axis each.
        Ruler.Source = Timeline;
        Channels.Source = Timeline;

        Timeline.ProgrammeSelected += (_, e) =>
        {
            if (DataContext is GuidePageViewModel vm)
            {
                vm.SelectProgramme(e.Row, e.Programme);
            }
        };

        Timeline.ProgrammeActivated += async (_, e) =>
        {
            if (DataContext is GuidePageViewModel vm)
            {
                await vm.ActivateAsync(e.Row, e.Programme);
            }
        };

        NowButton.Click += (_, _) => Timeline.ScrollToNow();

        // Opening the guide anywhere other than "now" would be perverse.
        Timeline.AttachedToVisualTree += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Timeline.ScrollToNow, Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
