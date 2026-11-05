namespace Haven.Core;

/// <summary>
/// Which painting texture (bgcommon/hou/indoor/pic/ta/NNNN) draws each Picture row. The textures keep the paintings'
/// original release order while the sheet's rows were renumbered, and no sheet column or client format string holds
/// the number, so the mapping was found by matching every painting's own image (Picture.Image, ui/icon/08xxxx) to the
/// textures pixel by pixel, each texture used once. It falls into runs of a constant offset, plus three paintings
/// slotted in elsewhere. The furnishing audit re-checks it against the installed images; a row it does not know (a
/// painting from a later patch) is not offered until the table is extended.
/// </summary>
public static class PaintingTextures
{
    // (first row, last row, texture − row), in row order.
    private static readonly (uint First, uint Last, int Offset)[] Runs =
    [
        (1, 31, 0), (32, 79, 1), (80, 83, 921), (84, 84, -52), (85, 99, 921), (100, 100, 905), (101, 145, 42),
        (146, 207, -65), (208, 269, -20), (270, 273, 751), (274, 286, -24), (287, 287, 8), (288, 319, -25), (354, 398, -58),
    ];

    public static uint? Texture(uint pictureRow)
    {
        foreach (var (first, last, offset) in Runs)
            if (pictureRow >= first && pictureRow <= last) return (uint)(pictureRow + offset);
        return null;
    }

    public static string Material(uint texture) => $"bgcommon/hou/indoor/pic/ta/{texture:0000}/material/pic_ta_2{texture:0000}a.mtrl";
}
