using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Haven.Core;
using static Haven.HavenTokens;

namespace Haven;

// Custom pictures in frames (HousingController.Content): the owner's Upload, their originals in the painting picker
// (each framed for the frame it goes in), Adjust per frame, the one-time content agreement, a frame's Portrait toggle,
// and a guest's prompt to load a home's pictures.
internal sealed partial class FurnishingEditor
{
    /// <summary>Opens the file picker; receives the chosen image's path.</summary>
    public Action<Action<string>>? PickPicture { get; set; }

    private readonly record struct Thumb(IDalamudTextureWrap Wrap, int Width, int Height);
    private readonly Dictionary<string, Task<Thumb?>> pictureThumbs = [];
    private string[] customShown = [], customOriginals = [];
    private string? deletingPicture;
    private Guid agreementFor;
    // What to do once the content agreement is accepted (the upload that asked for it).
    private Action? afterAgreement;
    private bool agreementPending;

    // The pictures the picker offers a frame, each framed for this frame when chosen: the owner's originals, or for a
    // decorator the home's own pictures.
    private void RefreshCustomShown(Furnishing item) => customOriginals = housing.CanFurnish ? housing.PictureOriginals().ToArray() : [];

    private Thumb? PictureThumb(string hash)
    {
        if (!pictureThumbs.TryGetValue(hash, out var task))
            pictureThumbs[hash] = task = Task.Run<Thumb?>(() => housing.PictureThumbnail(hash) is {} thumb
                ? new Thumb(textures.CreateFromRaw(RawImageSpecification.Rgba32(thumb.Width, thumb.Height), thumb.Rgba, "Haven picture " + hash[..12]), thumb.Width, thumb.Height)
                : null);
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    // A thumbnail fitted inside a box, keeping its shape.
    private static void DrawThumb(ImDrawListPtr draw, Thumb thumb, Vector2 min, Vector2 size)
    {
        var scale = Math.Min(size.X / thumb.Width, size.Y / thumb.Height);
        var fitted = new Vector2(thumb.Width, thumb.Height) * scale; var at = min + (size - fitted) / 2;
        draw.AddImageRounded(thumb.Wrap.Handle, at, at + fitted, Vector2.Zero, Vector2.One, HavenUi.U(Vector4.One), Px(RadiusField));
    }

    // Under the Painting button: the frame's way up, where the picture stands, Upload or Adjust for the owner, and
    // Remove.
    private void DrawCustomPictureControls(Furnishing item, FurnitureEntry entry)
    {
        if (entry.TurnsUpright)
        {
            var portrait = item.Portrait;
            if (HavenKit.Toggle("Portrait##frame-portrait", ref portrait)) housing.Run(() => housing.SetPortrait(item.Id, portrait));
            if (item.Portrait && item.Picture != 0)
                HavenKit.Caption("The game's paintings are landscape, so they lie on their side in a portrait frame.");
        }
        if (item.CustomPicture != null && !housing.CustomPictureReady(item))
            HavenKit.Caption(housing.CustomPicturesNeedPenumbra ? "Custom pictures need Penumbra, which isn't running." : "Loading the picture…");
        var gap = false;
        if (housing.CanAddCustomPictures)
        {
            if (HavenKit.Button("Upload picture…##custom-upload", ButtonKind.Secondary))
            {
                if (housing.AgreementAccepted) PickFor(item);
                else { agreementFor = item.Id; afterAgreement = () => PickFor(item); ImGui.OpenPopup("##content-agreement"); }
            }
            gap = true;
        }
        // Adjusting changes only this frame's framing: nothing is uploaded.
        if (item.CustomPicture is {} placed && housing.CanReframe(item))
        {
            if (gap) ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Adjust…##custom-adjust", ButtonKind.Secondary)) Frame(item, Guarded(() => housing.FrameOriginal(placed, item)), placed);
            gap = true;
        }
        if (item.CustomPicture != null && housing.CanFurnish)
        {
            if (gap) ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Remove##custom-remove", ButtonKind.Ghost)) housing.Run(() => housing.SetCustomPicture(item.Id, null));
        }
        if (agreementPending) { ImGui.OpenPopup("##content-agreement"); agreementPending = false; }
        DrawAgreement(item);
        DrawFraming();
    }

    // A new upload replaces nothing in the owner's pictures; the frame's earlier picture is cleaned up once unused.
    private void PickFor(Furnishing item) => PickPicture?.Invoke(path => Frame(item, Guarded(() => housing.ImportOriginal(path)), item.CustomPicture));

    // Starts loading a picture to frame; a refusal (no agreement, not the owner) shows as the dialog's message.
    private static Task<(string Original, PictureSource Source, PictureLayout? Layout)> Guarded(Func<Task<(string, PictureSource, PictureLayout?)>> start)
    {
        try { return start(); }
        catch (InvalidOperationException ex) { return Task.FromException<(string, PictureSource, PictureLayout?)>(ex); }
    }

    private void DrawAgreement(Furnishing item)
    {
        if (!HavenKit.BeginDialog("##content-agreement", "Before you add your own pictures or music", 520, true)) return;
        HavenKit.Caption("Haven keeps the pictures and music you upload and shares them with everyone who visits your homes and agrees to load custom content. You are responsible for what you upload.");
        HavenKit.Caption("•  Adult content is fine, as long as everyone shown is an adult.");
        HavenKit.Caption("•  Never upload anything involving minors, or anything illegal, including images or music you have no right to share and private images of anyone without their consent.");
        HavenKit.Caption("•  Haven may remove anything uploaded, and stop uploads from anyone who breaks these rules.");
        HavenKit.Caption("What your homes stop using is deleted after 30 days.");
        HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth("I agree") + Px(Space2));
        if (HavenKit.Button("Cancel##agreement", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("I agree##agreement", ButtonKind.Primary))
        {
            housing.Run(housing.AcceptAgreement);
            if (item.Id == agreementFor) afterAgreement?.Invoke();
            afterAgreement = null;
            ImGui.CloseCurrentPopup();
        }
        HavenKit.EndDialog();
    }

    // Framing ------------------------------------------------------------

    // The picture being framed for one frame: loaded off the game thread, previewed from a texture of at most 2048
    // pixels a side, and composed by PictureCanvas when placed.
    private sealed class Framing(Guid furnishing, bool portrait, Task<(string Original, PictureSource Source, PictureLayout? Layout)> loading, string? replaces)
    {
        public readonly Guid Furnishing = furnishing;
        public readonly string? Replaces = replaces;
        public readonly bool Portrait = portrait;
        public readonly Task<(string Original, PictureSource Source, PictureLayout? Layout)> Loading = loading;
        public string Original = "";
        public PictureSource? Source;
        public PictureLayout Layout = new();
        public Task<IDalamudTextureWrap>? Preview;
        public bool OpenPending = true, Shown;
        public void Dispose() => Preview?.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }
    private Framing? framing;
    private float framingScroll;

    // replaces: the picture being framed again, which the new one takes the place of.
    private void Frame(Furnishing item, Task<(string, PictureSource, PictureLayout?)> loading, string? replaces = null)
    {
        framing?.Dispose();
        framing = new(item.Id, item.Portrait, loading, replaces);
    }

    private void DrawFraming()
    {
        if (framing is not {} f) return;
        if (f.OpenPending) { ImGui.OpenPopup("##picture-framing"); f.OpenPending = false; }
        if (!HavenKit.BeginDialog("##picture-framing", "Frame your picture", 620, true))
        {
            // Closed (Cancel, Place or a click outside): let the preview go.
            if (f.Shown) { f.Dispose(); framing = null; }
            return;
        }
        f.Shown = true;
        if (!f.Loading.IsCompleted) HavenKit.Caption("Opening the picture…");
        else if (!f.Loading.IsCompletedSuccessfully)
            HavenKit.Caption(f.Loading.Exception?.GetBaseException().Message ?? "That picture could not be opened.");
        else
        {
            if (f.Source == null)
            {
                var (original, source, layout) = f.Loading.Result;
                f.Original = original; f.Source = source; f.Layout = layout ?? new();
                f.Preview = Task.Run(() =>
                {
                    var factor = (int)Math.Ceiling(Math.Max(source.Width, source.Height) / 2048.0);
                    int w = source.Width, h = source.Height; var pixels = source.Rgba;
                    if (factor > 1) pixels = PictureCanvas.Reduce(source.Rgba, source.Width, source.Height, factor, out w, out h);
                    return textures.CreateFromRaw(RawImageSpecification.Rgba32(w, h), pixels, "Haven picture framing");
                });
            }
            DrawFramingView(f);
        }
        ImGui.Dummy(Px(0, Space1));
        var ready = f.Source != null;
        const string place = "Place picture";
        HavenKit.AlignRight(HavenKit.ButtonWidth("Cancel") + HavenKit.ButtonWidth(place) + Px(Space2));
        if (HavenKit.Button("Cancel##framing", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
        ImGui.SameLine(0, Px(Space2));
        ImGui.BeginDisabled(!ready);
        if (HavenKit.Button(place + "##framing", ButtonKind.Primary) && f.Source is {} placed)
        {
            var layout = f.Layout;
            var original = f.Original;
            housing.Run(() => housing.PlaceCustomPicture(f.Furnishing, original, layout));
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        HavenKit.EndDialog();
    }

    // The frame's view, with the picture placed as it will hang: drag to move it, scroll to zoom about the pointer.
    private void DrawFramingView(Framing f)
    {
        var source = f.Source!; var layout = f.Layout;
        int iw = layout.Turns % 2 == 0 ? source.Width : source.Height, ih = layout.Turns % 2 == 0 ? source.Height : source.Width;
        var (vw, vh) = PictureCanvas.View(f.Portrait);
        // The preview shrinks on short screens, so the whole dialog (controls, Place) stays visible.
        var tall = Math.Clamp(ImGui.GetMainViewport().Size.Y - Px(460), Px(180), f.Portrait ? Px(360) : Px(336));
        var screen = f.Portrait ? new Vector2(tall * 3 / 5, tall) : new Vector2(Math.Min(Px(560), tall * 5 / 3), Math.Min(Px(560), tall * 5 / 3) * 3 / 5);
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (avail - screen.X) / 2));
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##framing-view", screen);
        var hovered = ImGui.IsItemHovered(); var io = ImGui.GetIO();
        var fit = PictureCanvas.FitZoom(iw, ih, f.Portrait);
        double minZoom = Math.Max(PictureLayout.MinZoom, fit / 2), maxZoom = 8;
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0))
            layout = layout with { X = Math.Clamp(layout.X + io.MouseDelta.X / screen.X, -2, 3), Y = Math.Clamp(layout.Y + io.MouseDelta.Y / screen.Y, -2, 3) };
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            if (io.MouseWheel != 0)
            {
                // The wheel zooms the picture; ImGui already scrolled the dialog with it this frame, so undo that.
                ImGui.SetScrollY(framingScroll);
                // Zoom about the pointer: the picture point under it stays put.
                var zoom = Math.Clamp(layout.Zoom * Math.Pow(1.12, io.MouseWheel), minZoom, maxZoom);
                double px = (io.MousePos.X - min.X) / screen.X, py = (io.MousePos.Y - min.Y) / screen.Y, ratio = zoom / layout.Zoom;
                layout = layout with { Zoom = zoom, X = Math.Clamp(px - (px - layout.X) * ratio, -2, 3), Y = Math.Clamp(py - (py - layout.Y) * ratio, -2, 3) };
            }
        }
        if (!hovered || io.MouseWheel == 0) framingScroll = ImGui.GetScrollY();
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(min, min + screen, true);
        draw.AddRectFilled(min, min + screen, HavenUi.U(layout.Dark ? new Vector4(0, 0, 0, 1) : Vector4.One));
        if (f.Preview is { IsCompletedSuccessfully: true } preview)
        {
            var perUnit = screen.X / (float)vw;
            var size = new Vector2(iw, ih) * (float)(PictureCanvas.FillScale(iw, ih, f.Portrait) * layout.Zoom) * perUnit;
            var centre = min + new Vector2((float)layout.X * screen.X, (float)layout.Y * screen.Y);
            Vector2 tl = centre - size / 2, br = centre + size / 2;
            // Turned clockwise: each corner shows the source corner that many quarter turns back.
            Vector2[] corners = [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY];
            Vector2 Uv(int corner) => corners[((corner - layout.Turns) % 4 + 4) % 4];
            draw.AddImageQuad(preview.Result.Handle, tl, new(br.X, tl.Y), br, new(tl.X, br.Y), Uv(0), Uv(1), Uv(2), Uv(3), HavenUi.U(Vector4.One));
        }
        else draw.AddText(min + (screen - ImGui.CalcTextSize("…")) / 2, HavenUi.U(TextSecondary), "…");
        draw.PopClipRect();
        draw.AddRect(min, min + screen, HavenUi.U(Divider), 0, ImDrawFlags.None, Px(1));
        HavenKit.Caption("Drag to move, scroll to zoom. The frame shows exactly what is inside this box.");
        var zoomValue = (float)layout.Zoom;
        if (HavenKit.Slider("##framing-zoom", "Zoom", ref zoomValue, (float)minZoom, (float)maxZoom, $"{zoomValue * 100:0} %"))
            layout = layout with { Zoom = zoomValue };
        if (HavenKit.Button("Fill##framing", ButtonKind.Secondary)) layout = layout with { Zoom = 1, X = .5, Y = .5 };
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Fit##framing", ButtonKind.Secondary)) layout = layout with { Zoom = Math.Max(PictureLayout.MinZoom, fit), X = .5, Y = .5 };
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Rotate##framing", ButtonKind.Secondary)) layout = layout with { Turns = (layout.Turns + 1) % 4, Zoom = 1, X = .5, Y = .5 };
        ImGui.SameLine(0, Px(Space4));
        var dark = layout.Dark;
        if (HavenKit.Toggle("Black border##framing", ref dark, Px(170))) layout = layout with { Dark = dark };
        f.Layout = layout;
    }

    // Inside the painting picker, above the game's paintings.
    private void DrawCustomPictureTiles(Furnishing item)
    {
        var width = Px(PictureColumns * (PictureTile.X + 6));
        if (customOriginals.Length > 0)
        {
            var owner = housing.CanAddCustomPictures;
            HavenKit.SectionTitle(owner ? "Your pictures" : "This home's pictures", "", width);
            HavenKit.Caption($"Choose one to frame it for this {(item.Portrait ? "portrait" : "landscape")} frame.{(owner ? " Right-click one to delete it." : "")}", width);
            PictureTiles(item, customOriginals, "Frame it for this frame", original => Frame(item, Guarded(() => housing.FrameOriginal(original, item)), item.CustomPicture));
        }
        else if (!housing.CanAddCustomPictures) return;
        if (deletingPicture != null) { confirmDelete = deletingPicture; deletingPicture = null; ImGui.OpenPopup("##picture-delete"); }
        DrawDeletePicture();
        ImGui.Dummy(Px(0, Space2));
        HavenKit.SectionTitle("Paintings", "", Px(PictureColumns * (PictureTile.X + 6)));
    }

    // A row of picture tiles; choose runs on a click, and the owner can right-click one to delete it.
    private void PictureTiles(Furnishing item, string[] hashes, string tooltip, Action<string> choose)
    {
        for (var i = 0; i < hashes.Length; i++)
        {
            var hash = hashes[i];
            if (i % PictureColumns != 0) ImGui.SameLine(0, Px(6));
            var size = Px(PictureTile.X, PictureTile.Y);
            var selected = item.CustomPicture == hash;
            if (HavenKit.Card("##custom-" + hash, size, selected, out var min)) { choose(hash); ImGui.CloseCurrentPopup(); }
            var draw = ImGui.GetWindowDrawList(); var inset = Px(4, 4);
            if (PictureThumb(hash) is {} thumb) DrawThumb(draw, thumb, min + inset, size - inset * 2);
            else draw.AddText(min + (size - ImGui.CalcTextSize("…")) / 2, HavenUi.U(TextSecondary), "…");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip.Length > 0 ? tooltip : housing.HomePictures.Contains(hash) ? "Shown in this home" : "From your pictures");
            if (housing.CanAddCustomPictures && ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup("##custom-menu-" + hash);
            if (HavenKit.BeginMenu("##custom-menu-" + hash))
            {
                if (HavenKit.MenuItem("Delete picture…", danger: true)) deletingPicture = hash;
                ImGui.EndPopup();
            }
        }
    }

    private string? confirmDelete;
    private void DrawDeletePicture()
    {
        if (!HavenKit.BeginDialog("##picture-delete", "Delete this picture?", 440, true)) return;
        HavenKit.Caption("It is deleted from this PC. Haven refuses while a home on this PC shows it.");
        HavenKit.AlignRight(HavenKit.ButtonWidth("Keep") + HavenKit.ButtonWidth("Delete") + Px(Space2));
        if (HavenKit.Button("Keep##picture-delete", ButtonKind.Ghost)) ImGui.CloseCurrentPopup();
        ImGui.SameLine(0, Px(Space2));
        if (HavenKit.Button("Delete##picture-delete", ButtonKind.Danger) && confirmDelete is {} hash)
        {
            housing.Run(() =>
            {
                housing.DeleteOriginal(hash);
                customOriginals = customOriginals.Where(h => h != hash).ToArray();
                if (pictureThumbs.Remove(hash, out var task) && task.IsCompletedSuccessfully) task.Result?.Wrap.Dispose();
            });
            ImGui.CloseCurrentPopup();
        }
        HavenKit.EndDialog();
    }

    // A guest entering someone's home with custom pictures chooses once whether to load them.
    private void DrawPictureConsent()
    {
        if (!housing.CustomConsentPending) return;
        var viewport = ImGui.GetMainViewport();
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, Px(96)), ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.SetNextWindowSize(new(Px(440), 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space5)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space3));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow)); ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .96f });
        if (ImGui.Begin("Custom content###Haven.PictureConsent", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            using (HavenFonts.Use(HavenText.Title)) ImGui.TextUnformatted("Custom content");
            string[] kinds = [.. new[] { (housing.CustomConsentPictures, "pictures"), (housing.CustomConsentSongs, "music"), (housing.CustomConsentFurniture, "furniture"), (housing.CustomConsentAnimations, "animations") }.Where(k => k.Item1).Select(k => k.Item2)];
            var what = kinds.Length <= 1 ? kinds.FirstOrDefault() ?? "pictures" : string.Join(", ", kinds[..^1]) + " and " + kinds[^1];
            HavenKit.Caption($"{housing.CustomConsentHome} has {what} its owner uploaded. Haven downloads it only if you agree, and remembers your choice for this home.");
            if (housing.CustomPicturesNeedPenumbra) HavenKit.Caption("It needs Penumbra, which isn't running.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Not now") + HavenKit.ButtonWidth("Load it") + Px(Space2));
            if (HavenKit.Button("Not now##consent", ButtonKind.Ghost)) housing.Run(() => housing.DecideCustomPictures(false));
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Load it##consent", ButtonKind.Primary)) housing.Run(() => housing.DecideCustomPictures(true));
        }
        ImGui.End(); ImGui.PopStyleColor(); ImGui.PopStyleVar(3); HavenKit.PopTheme();
    }

    // A guest's downloads that failed outright: what didn't arrive and why, and whether to try again or give up.
    private void DrawDownloadFailures()
    {
        if (housing.CustomConsentPending || housing.FailedDownloads is not { Count: > 0 } failed) return;
        var viewport = ImGui.GetMainViewport();
        HavenKit.PushTheme();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X / 2, Px(96)), ImGuiCond.Always, new Vector2(.5f, 0));
        ImGui.SetNextWindowSize(new(Px(440), 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Px(Space5, Space5)); ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Px(Space2, Space3));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Px(RadiusWindow)); ImGui.PushStyleColor(ImGuiCol.WindowBg, Window with { W = .96f });
        if (ImGui.Begin("Downloads###Haven.FailedDownloads", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            HavenKit.KeepOnTop();
            using (HavenFonts.Use(HavenText.Title)) ImGui.TextUnformatted(failed.Count == 1 ? "Something didn't download" : $"{failed.Count} things didn't download");
            HavenKit.Caption("Haven stopped trying. Until it gets them, furniture shows as the original item, frames stay blank and songs and animations are skipped.");
            foreach (var item in failed.Take(5))
            {
                using (HavenFonts.Use(HavenText.Strong)) ImGui.TextUnformatted(item.What);
                HavenKit.Caption(item.Reason);
            }
            if (failed.Count > 5) HavenKit.Caption($"And {failed.Count - 5} more.");
            HavenKit.AlignRight(HavenKit.ButtonWidth("Give up") + HavenKit.ButtonWidth("Try again") + Px(Space2));
            if (HavenKit.Button("Give up##downloads", ButtonKind.Ghost)) housing.Run(housing.GiveUpDownloads);
            ImGui.SameLine(0, Px(Space2));
            if (HavenKit.Button("Try again##downloads", ButtonKind.Primary)) housing.Run(housing.RetryDownloads);
        }
        ImGui.End(); ImGui.PopStyleColor(); ImGui.PopStyleVar(3); HavenKit.PopTheme();
    }

    public void Dispose()
    {
        if (estateWatched) engine.HousingEstateTeleportAsked -= OnEstateTeleportAsked;
        foreach (var task in pictureThumbs.Values)
            if (task.IsCompletedSuccessfully) task.Result?.Wrap.Dispose();
        pictureThumbs.Clear();
        framing?.Dispose();
    }
}
