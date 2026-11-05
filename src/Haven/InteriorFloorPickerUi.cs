namespace Haven;

internal static class InteriorFloorPickerUi
{
    private static readonly int[] Ground = [0], Cottage = [0, 2], Large = [0, 1, 2];

    // Preserve native indices: the second cottage choice is basement (2), not upper (1).
    internal static ReadOnlySpan<int> FloorsForShell(string? code)
    {
        if (code == null) return [];
        if (code.EndsWith("i1", StringComparison.Ordinal)) return Cottage;
        if (code.EndsWith("i2", StringComparison.Ordinal) || code.EndsWith("i3", StringComparison.Ordinal)) return Large;
        if (code.EndsWith("i4", StringComparison.Ordinal) || code.EndsWith("i4_2", StringComparison.Ordinal)
            || code.EndsWith("i6", StringComparison.Ordinal)) return Ground;
        return []; // Workshops and unknown layouts have no supported native finish controls.
    }
}
