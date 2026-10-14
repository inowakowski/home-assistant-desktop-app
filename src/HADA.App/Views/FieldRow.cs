using Avalonia.Controls;

namespace HADA.App.Views;

/// <summary>
/// A row of a settings section: what it is in the first column, the field in the second. The field stops growing
/// where it would get wider than anything typed into it, and stays next to its label.
/// </summary>
public sealed class FieldRow : Grid
{
    public FieldRow()
    {
        ColumnDefinitions = [new ColumnDefinition(220, GridUnitType.Pixel), new ColumnDefinition(GridLength.Star) { MaxWidth = 460 }];
        Classes.Add("field");
    }

    // Styled as a grid, with the "field" class.
    protected override Type StyleKeyOverride => typeof(Grid);
}
