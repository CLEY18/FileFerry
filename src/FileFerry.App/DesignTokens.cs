namespace FileFerry.App;

/// <summary>
/// Sizes/spacing/radii: one unit in one place, no `#if` and no magic
/// numbers in XAML. All values are dp (= MAUI dip); baseline grid is 4 dp.
/// </summary>
public static class DesignTokens
{
    public const double Grid = 4;

    public const double Space4 = 4;

    public const double Space8 = 8;

    /// <summary>
    /// Card gutters (minimum).
    /// </summary>
    public const double Space12 = 12;

    /// <summary>
    /// Screen margins; maximum padding inside cards.
    /// </summary>
    public const double Space16 = 16;

    public const double Space20 = 20;

    /// <summary>
    /// Spacing between sections.
    /// </summary>
    public const double Space24 = 24;

    public const double Space32 = 32;

    public const double ScreenPadding = Space16;

    public const double SectionSpacing = Space24;

    /// <summary>
    /// Minimum touch target per the Nextcloud guidelines.
    /// </summary>
    public const double TouchTargetMin = 34;

    /// <summary>
    /// Touch target on the Android canvas (rows, action icons).
    /// </summary>
    public const double TouchTargetAndroid = 48;

    /// <summary>
    /// List row height (20 dp icon + two text lines).
    /// </summary>
    public const double ListRowHeight = 64;

    public const double TopAppBarHeight = 56;

    public const double FabSize = 56;

    public const double FabMargin = Space16;

    /// <summary>
    /// Bottom clearance full-height lists give their scroll content so the last card
    /// scrolls free of the FAB (its size plus its edge margin).
    /// </summary>
    public const double FabClearance = FabSize + FabMargin;

    public const double FabRadius = FabSize / 2;

    /// <summary>
    /// Button height as a minimum, never a fixed one: the label must survive a large system font,
    /// and the touch target on the Android canvas is 48 dp.
    /// </summary>
    public const double ButtonMinHeight = TouchTargetAndroid;

    /// <summary>
    /// Reading-width cap for single-column screens in a wide window (tablet, unfolded foldable).
    /// </summary>
    public const double ContentMaxWidth = 640;

    public const double InputHeight = 48;

    public const double RadiusChip = 4;

    /// <summary>
    /// Radius of buttons, input fields and card rows.
    /// </summary>
    public const double RadiusControl = 8;

    /// <summary>
    /// Radius of dialogs and bottom sheets.
    /// </summary>
    public const double RadiusDialog = 12;

    /// <summary>
    /// Status-chip radius ("pill"): as tall as the chip itself — a deliberately
    /// oversized value.
    /// </summary>
    public const double RadiusPill = 999;

    /// <summary>
    /// Icons: list glyphs, buttons, chips, status badges - the floor for a meaningful glyph.
    /// </summary>
    public const double IconMedium = 24;

    /// <summary>
    /// EmptyState icon.
    /// </summary>
    public const double IconEmpty = 56;

    public const double IconEmptyMin = 48;

    public const double IconEmptyMax = 64;
}
