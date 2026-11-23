namespace FileFerry.App.Views;

/// <summary>
/// Pair status chip: pill container, Material Symbols glyph + localized
/// text. Colors resolve by token name from application resources (never hex), so
/// <see cref="Theming.ThemeMode.Dynamic"/> applies to the badge too.
/// </summary>
public sealed class SyncStatusBadge : ContentView
{
    /// <summary>
    /// Chip state: drives background, glyph and glyph color.
    /// </summary>
    public static readonly BindableProperty StateProperty = BindableProperty.Create(
        nameof(State), typeof(ViewModels.PairStatus), typeof(SyncStatusBadge),
        ViewModels.PairStatus.Idle, propertyChanged: (b, _, _) => ((SyncStatusBadge)b).Apply());

    /// <summary>
    /// Chip text (localized by the VM; when empty — the default status text).
    /// </summary>
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(SyncStatusBadge), string.Empty,
        propertyChanged: (b, _, n) => ((SyncStatusBadge)b)._label.Text = (string?)n ?? string.Empty);

    /// <summary>
    /// Theme stamp (the counter of palette re-maps): the chip resolves token names to concrete
    /// colors, so it re-applies them whenever the stamp changes — otherwise a live theme switch
    /// would leave the chip wearing the previous palette.
    /// </summary>
    public static readonly BindableProperty ThemeStampProperty = BindableProperty.Create(
        nameof(ThemeStamp), typeof(int), typeof(SyncStatusBadge), 0,
        propertyChanged: (b, _, _) => ((SyncStatusBadge)b).Apply());

    private readonly Border _border;
    private readonly Label _glyph;
    private readonly Label _label;

    /// <summary>
    /// Creates an empty chip (the state arrives via binding).
    /// </summary>
    public SyncStatusBadge()
    {
        _glyph = new Label
        {
            FontFamily = GlyphMapping.FontFamily,
            FontSize = DesignTokens.IconMedium,
            VerticalTextAlignment = TextAlignment.Center,
        };
        // beside the status word the glyph is decoration: a reader must not speak its codepoint
        A11y.SetIsDecorative(_glyph, true);
        // the caption role carries the size and leading; the literal here would drift from the type system
        _label = new Label { VerticalTextAlignment = TextAlignment.Center };
        _label.SetDynamicResource(StyleProperty, "ChipLabel");
        _border = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = DesignTokens.RadiusPill },
            Padding = new Thickness(DesignTokens.Space12, DesignTokens.Space4),
            Content = new HorizontalStackLayout
            {
                Spacing = DesignTokens.Space8,
                Children = { _glyph, _label },
            },
        };
        Content = _border;
        Apply();
    }

    /// <summary>
    /// The state the chip is drawn for.
    /// </summary>
    public ViewModels.PairStatus State
    {
        get => (ViewModels.PairStatus)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>
    /// Overridden chip text (e.g. "Conflicts: 3").
    /// </summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Current palette re-map counter (bound to the theme stamp).
    /// </summary>
    public int ThemeStamp
    {
        get => (int)GetValue(ThemeStampProperty);
        set => SetValue(ThemeStampProperty, value);
    }

    /// <summary>
    /// Status: background token, glyph, glyph-color token.
    /// </summary>
    private static (string Background, string Glyph, string GlyphColor) SpecFor(ViewModels.PairStatus status) => status switch
    {
        ViewModels.PairStatus.Syncing => ("PrimaryContainer", GlyphMapping.Sync, "PrimaryText"),
        ViewModels.PairStatus.Success => ("Transparent", GlyphMapping.CheckCircle, "StatusSuccess"),
        ViewModels.PairStatus.Conflict => ("StatusWarningContainer", GlyphMapping.Merge, "StatusWarning"),
        ViewModels.PairStatus.Error => ("StatusErrorContainer", GlyphMapping.Error, "StatusError"),
        ViewModels.PairStatus.Offline => ("SurfaceVariant", GlyphMapping.CloudOff, "TextSecondary"),
        _ => ("SurfaceVariant", GlyphMapping.Sync, "TextSecondary"),
    };

    /// <summary>
    /// Applies the status; "Transparent" means no background (Success).
    /// </summary>
    private void Apply()
    {
        var (background, glyph, glyphColor) = SpecFor(State);
        var tonal = background != "Transparent";
        _border.BackgroundColor = tonal ? Token(background) : Colors.Transparent;
        // A tonal pill needs inner breathing room; a bare chip must sit flush with the
        // surrounding text column — without a visible background its horizontal padding
        // would only read as a misalignment of the glyph.
        _border.Padding = tonal
            ? new Thickness(DesignTokens.Space12, DesignTokens.Space4)
            : new Thickness(0, DesignTokens.Space4);
        _glyph.Text = glyph;
        _glyph.TextColor = Token(glyphColor);
        if (string.IsNullOrEmpty(_label.Text))
        {
            _label.Text = ViewModels.PairListItem.StatusTextFor(State);
        }
    }

    /// <summary>
    /// Color token from application resources (values maintained by <c>ThemeService</c>).
    /// </summary>
    private static Color Token(string key) =>
        Application.Current is { } app && app.Resources.TryGetValue(key, out var value) && value is Color color
            ? color
            : Colors.Transparent;
}
