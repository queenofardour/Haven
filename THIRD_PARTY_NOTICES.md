# Third-party code

Haven is distributed under AGPL-3.0-or-later.

## HMapSync native engine and wire protocol

Source: https://github.com/Enceladeum/HMapSync

Author: Enceladeum and upstream contributors. Snapshot: `8140dbb04cfbfa59da9ff0c3f4e787978d368303`.

The files under `src/Haven/Native/Upstream/` and supporting embedded data derive from that snapshot. Original comments and implementation are retained. Changes in this project: namespace changed from HMSync to HavenNative; plugin entry point changed to an internal engine; housing UI replaces the default main UI; upstream relay defaults removed; inbound zone requests restricted to housing; loader preflight added; Haven adds a furniture snapshot message, larger bounded frame reassembly, a connection timeout, untrusted MessagePack settings, and serialized WebSocket writes. `NativeEngine.Housing.cs` is Haven's integration bridge. The initial alpha retains the native backend to preserve its load/restore behavior; it has not been rewritten as a small independent loader.

Version 0.2 also narrows delayed suppression to placed furniture, preserves fixture interaction arrows, intercepts blocked housing targets for Haven's return path, adds local seat/rest actions and orchestrion volume, and negotiates a Haven housing protocol revision (revision 3 as of 0.5 for per-item scale). Haven's separate runtime reads installed prefabs and uses installed FFXIVClientStructs interfaces for collision and interior previews.

The upstream license text is in LICENSE. See the upstream repository for its history and component acknowledgements. No game files are redistributed. The optional baked day/night keyframe library is omitted.

Version 0.6 adds bounded callbacks to the inherited emote guard/executor hooks for nearby Haven furniture markers, initializes the local chair/bed pose family, and leaves native pose cycling on its original path. These callbacks are private-scene-only; they do not register replacement game text commands.

Version 0.6.3 uses housing protocol revision 4 for natural-duration music schedules. It replaces inherited direct BGM playback-ID writes with FFXIVClientStructs BGM controls and adds paired native orchestrion queue cleanup, verified by read-only executable inspection. Duration readers use installed Lumina files and independently parse Vorbis/HCA sample metadata. Format references are credited in docs/updates/UPDATE-0.6.3.md; no codec decoder or game audio is bundled.

Version 0.7.0 adds Haven's experimental blank-canvas mode. The inherited zone service gains a private-apartment-only shell suppression entry point using its existing leaf visibility, live instance traversal, collision masks and return restoration. It does not deactivate or destroy shared-group containers. Unowned collider IDs are excluded so Haven's generated safety platform survives suppression. Custom arrival/recovery, generated platform rendering, creation UI and snapshot validation are Haven additions; no game model or collision files are bundled.

## Custom music: NLayer, NVorbis and OggVorbisEncoder

Haven bundles these unmodified NuGet assemblies to read a player's chosen song and write the Ogg Vorbis the orchestrion plays:

- **NLayer** 3.0.0 (https://github.com/naudio/NLayer), by Mark Heath and Andrew Ward, © Mark Heath 2026, MIT License: MP3 decoding.
- **NVorbis** 0.10.5 (https://github.com/NVorbis/NVorbis), Copyright (c) 2020 Andrew Ward, MIT License: Ogg Vorbis decoding.
- **OggVorbisEncoder** 1.2.2 (https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder), by Steve Lillis, MIT License: a C# port of the Xiph.Org Foundation's libvorbis encoder (BSD licence).

`src/Haven.Core/Music/FlacDecoder.cs` is Haven's own FLAC decoder, written from RFC 9639.

## VFXEditor (SCD music layout)

Source: https://github.com/0ceal0t/Dalamud-VFXEditor. Copyright 2021 Michael Kaminsky. MIT License.

`src/Haven.Core/Music/ScdMusic.cs` follows VFXEditor's reading of the game's SCD music container (`VFXEditor/Formats/ScdFormat`: the header, the audio entry and its plain Vorbis sub-header, and the Ogg page seek table) to wrap a custom song in an SCD built from the player's own game data. No VFXEditor code or game file is bundled.

## Penumbra (mod format)

Source: https://github.com/xivdev/Penumbra. Studied to read furniture mods for custom furniture (`src/Haven.Core/Furniture/PenumbraMod.cs`): the mod folder and .pmp layout, `meta.json`, `default_mod.json` (`Files`, `FileSwaps`), `group_NNN_*.json` groups with `Type`, `DefaultSettings`, `Priority` and `Options` (`Mods/SubMods/SubMod.cs`, `Mods/Groups`), and how replaced files are loaded under their resolved path (`Interop/Hooks/ResourceLoading/ResourceLoader.cs`). No Penumbra code is copied or bundled; Haven uses Penumbra's public IPC as before.

## BypassEmote

Source: https://github.com/Aspher0/BypassEmote

Author: Aspher0 and contributors. License: AGPL-3.0. Snapshot: `9de1f14699b053c9aeb56e31102773bfb98c0124`.

`src/Haven/Native/EmoteWindowService.cs` adapts its emote window handling (`Helpers/EmoteAddonValues.cs`, `Helpers/EmoteListDimmer.cs`, `Helpers/ContextMenuValues.cs` and parts of `Service.EmoteUi.cs`): the window's value layout, adding unowned emotes to the list and search results, clearing their locked flags, lighting rows the game dims, and enabling the context menu's Execute. Changes in this project: rewritten without NoireLib, active only inside a Haven home, no hotbar painting or history writes, and playback stays with Haven's own emote hooks.

## Brio and Ktisis (camera hook points)

Sources: https://github.com/Etheirys/Brio (snapshot `40ef9aab042c7ae33f2c4cc38f997b0fd5ef9fe4`) and https://github.com/ktisis-tools/Ktisis (snapshot `774e61399df57108feea1f86dee54fc5bdb42877`). Authors: Etheirys and the Ktisis team, with contributors. License: GPL-3.0 (both).

`src/Haven/Native/NativeEngine.BuildCamera.cs` (the free building camera) uses where these projects hook the game camera: offsetting the camera's position and look-at after its update (Brio's camera service) and the camera collision signature with its +0x218 distance pair (Ktisis's camera module). No code was copied; Haven's camera, its input and its turn handling are its own.

## Stagehand

Source: https://github.com/universalconquistador/Stagehand

Author: UniversalConquistador and upstream contributors. API and definition packages: 0.5.5, AGPL-3.0-or-later. Haven consumes the published packages over Dalamud IPC. Stagehand supplies imported layout/light rendering and optional source-stage editing. Haven supplies its own catalogue and interface, and version 0.3 adds snapshot editing of imported Stagehand models/groups/lights. Version 0.5 adds Haven-owned catalogue model rendering with corrected client stain writes. Its create/transform/readiness/destruction sequence follows the public Stagehand `LiveObjectService.CreateBgObject`, `LiveObject` and `LiveBgObject` lifetime pattern and FFXIVClientStructs `BgObject` interfaces. No installed Stagehand assembly is modified. No reference artwork or font files from studied interfaces are bundled.

Version 0.66 adds Haven-drawn furnishing lights (`src/Haven/FurnitureLights.cs`), an optional alternative to Stagehand's: their creation, setup (the render light's transform and unlimited range), field writes, projected texture handling, per-frame material update and destruction follow Stagehand 0.5.5's `LiveObjectService.CreateLight` and `LiveLight` (snapshot `48b6f54`), with FFXIVClientStructs' `Scene.Light` and `Render.Light` interfaces. Haven-drawn furnishing effects (`src/Haven/FurnitureEffects.cs`) follow Stagehand 0.5.5's `LiveObjectService.CreateVfx` and `LiveVfxObject` the same way, with FFXIVClientStructs' `Scene.VfxObject`. Haven-played furnishing sounds (`src/Haven/FurnitureSounds.cs`) follow Stagehand 0.5.5's `LiveObjectService.CreateSound` and `LiveSoundObject` (the arguments its compiled calls pass, its finished-sound test and its release), with FFXIVClientStructs' `SoundManager` and `SoundData`.

## Other dependencies

RTyping (apetih/rtyping, https://github.com/apetih/rtyping) was studied at commit fcfe71f for cursor/text input detection and overhead indicator concepts during the 0.9.4 fixes. No source code or artwork from it is bundled; Haven's input reader, state protocol and speech-bubble rendering are independently implemented. FFXIVClientStructs' documented UIState/QuestManager layouts and the installed client's read-only disassembly informed strict emote ownership checks. These checks do not modify game unlock data.


OrchestrionPlugin (perchbirdd fork, https://github.com/perchbirdd/OrchestrionPlugin, MIT) was studied at commit e103156 for native playback ownership and playlist behavior during the 0.9.1 music fixes. No source code or assets from it are bundled.

## StbImageSharp and StbImageWriteSharp

StbImageSharp 2.30.16 (https://github.com/StbSharp/StbImageSharp) and StbImageWriteSharp 1.16.7 (https://github.com/StbSharp/StbImageWriteSharp), C# ports by the StbImageSharp team (NuGet author StbImageSharpTeam) of Sean Barrett's stb_image and stb_image_write. Each is dual-licensed under the Unlicense (public domain) or the MIT License, at the user's choice; Haven uses them under the MIT License. The original stb libraries are Copyright (c) 2017 Sean Barrett under the same MIT alternative. Haven bundles the unmodified `StbImageSharp.dll` and `StbImageWriteSharp.dll` NuGet assemblies to read an owner's chosen image and to write the JPEG a picture frame shows.

MIT License: Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Inter

Source: https://github.com/rsms/inter, release 4.1. Copyright (c) 2016 The Inter Project Authors. Licensed under the SIL Open Font License 1.1; the full licence is in `src/Haven/Fonts/Inter-LICENSE.txt` and ships beside the plugin as `Inter-LICENSE.txt`. Haven embeds the unmodified `Inter-Regular.ttf` and `Inter-SemiBold.ttf` as its interface typeface. The fonts are not sold on their own.

## Font Awesome

The owner-tool wrench uses Font Awesome Free through Dalamud's existing icon font. The standalone preview reads a separately obtained Font Awesome Free Solid font; no Font Awesome binary is bundled in Haven's plugin/source packages. Font Awesome Free's icons/font retain their CC BY 4.0 / SIL OFL 1.1 licensing. Character Select+ and Browsingway were studied as design/architecture references; no source code, imagery or fonts from those plugins were copied.

Dalamud.NET.Sdk (goatcorp), Glamourer.Api (Ottermandias and contributors), MessagePack for C# (neuecc and contributors), and transitive dependencies retain their respective licenses. Consult the pinned packages and package metadata for full notices.
