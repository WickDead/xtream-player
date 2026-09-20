using Avalonia.Controls;
using XtreamPlayer.App.Behaviors;
using XtreamPlayer.App.ViewModels;

namespace XtreamPlayer.App.Views;

public partial class VodPage : UserControl
{
    /// <summary>Tile width plus its margins, matching the item template.</summary>
    private const double TileWidth = 176;

    public VodPage()
    {
        InitializeComponent();

        // The view owns the column count because only it knows how wide it is.
        // Re-chunking on resize is what keeps the grid filling the window
        // instead of leaving a ragged gap down one side.
        PosterRows.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);
    }

    private void UpdateColumns(double width)
    {
        if (DataContext is not VodPageViewModel page || width <= 0)
        {
            return;
        }

        var columns = Math.Max(1, (int)(width / TileWidth));

        page.ColumnCount = columns;

        // ScrollAnchor needs this to re-project a saved index when the window
        // was resized while another page was on top.
        ScrollAnchor.SetColumns(PosterRows, columns);
    }
}
