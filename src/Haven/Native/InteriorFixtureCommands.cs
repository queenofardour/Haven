using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace HavenNative;

internal static unsafe class InteriorFixtureCommands
{
    // Clear consumed transient commands only. Preserve windows/doors, other
    // exterior fields, native short fields and the house-wide LightLevel.
    internal static void Clear(IndoorAreaLayoutData* data)
    {
        data->Floor0.Part0 = data->Floor0.Part3 = data->Floor0.Part4 = -1;
        data->Floor1.Part0 = data->Floor1.Part3 = data->Floor1.Part4 = -1;
        data->Floor2.Part0 = data->Floor2.Part3 = data->Floor2.Part4 = -1;
        // FFCS calls slot 3 Exterior. On indoor layouts its Part4 is the
        // mansion's central chandelier, separate from all three floor lights.
        data->Exterior.Part4 = -1;
    }
}
