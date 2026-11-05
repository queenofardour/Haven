namespace Haven;

internal sealed record HomeLayoutOption(uint Id, string Name, string Subtype, string District, int Size);

internal static class HomeLayoutPickerUi
{
    // Inns (territory codes ending "ti") come first: the smallest rooms.
    internal static readonly (string Name, string Suffix)[] Sizes =
        [("Inn", "ti"), ("Apartment", "i4_2"), ("Cottage", "i1"), ("House", "i2"), ("Mansion", "i3"), ("Custom (experimental)", "__custom__")];
    /// <summary>The custom canvas's size: the last.</summary>
    internal const int Custom = LauncherUi.CustomSize;

    internal static int SizeForCode(string code) => Array.FindIndex(Sizes, x => code.EndsWith(x.Suffix, StringComparison.Ordinal));
}
