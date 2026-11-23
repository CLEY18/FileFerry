using Microsoft.Maui.Layouts;

namespace FileFerry.App.Views;

/// <summary>
/// A row that gives every child an equal-width cell — the skeleton of a segmented choice:
/// mutually exclusive options must read as one control with equal weight, not as chips sized
/// by their text. Measured narrow, the row wants cells as wide as the widest child; given more
/// width it stretches all cells equally to fill.
/// </summary>
public sealed class SegmentedRow : Layout
{
    internal const double CellGap = 2;

    protected override LayoutManager CreateLayoutManager() => new EqualCellLayoutManager(this);

    private sealed class EqualCellLayoutManager : LayoutManager
    {
        private readonly SegmentedRow _row;

        public EqualCellLayoutManager(SegmentedRow owner)
            : base(owner) => _row = owner;

        public override Size Measure(double widthConstraint, double heightConstraint)
        {
            double cell = 0;
            double rowHeight = 0;
            foreach (var child in _row.Children)
            {
                child.Measure(double.PositiveInfinity, heightConstraint);
                cell = Math.Max(cell, child.DesiredSize.Width);
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            double width = _row.Children.Count == 0
                ? 0
                : cell * _row.Children.Count + SegmentedRow.CellGap * (_row.Children.Count - 1);
            return new Size(width, rowHeight);
        }

        public override Size ArrangeChildren(Rect bounds)
        {
            var children = _row.Children;
            if (children.Count == 0)
            {
                return bounds.Size;
            }

            double cell = (bounds.Width - SegmentedRow.CellGap * (children.Count - 1)) / children.Count;
            double x = bounds.X;
            foreach (var child in children)
            {
                child.Arrange(new Rect(x, bounds.Y, Math.Max(cell, 0), bounds.Height));
                x += cell + SegmentedRow.CellGap;
            }

            return bounds.Size;
        }
    }
}
