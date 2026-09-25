# unityviewer
Tool to browse unity 3D resource files, viewand export them

A Windows (WPF, .NET 10) browser for Unity asset bundles (`.unity3d`, `.bundle`, `.assets`), with its own
parser for the UnityFS container and serialised files, so it works even when a game strips the Unity version
from its bundles.

## Features

- **Browse** a file or a whole folder (including subfolders) in a tree: bundle entries, types and named objects.
- **Types tab**: list all models, textures (Texture2D and Sprite), sounds or animations across every open file.
- **Find Dependencies**: bundles refer to each other only by internal CAB names; this scans a folder (headers
  only, very fast) for the bundles the selected file needs and opens them alongside it.
- **Preview**
  - Texture2D and Sprite (DXT1/3/5, BC4/5/6H/7, Crunch (DXT and ETC), ETC1/ETC2 and the common uncompressed
    formats), with fit/zoom and alpha.
  - AudioClip playback (FMOD FSB5: Vorbis and PCM), with waveform and seeking.
  - Mesh in a 3D viewer (orbit, pan, zoom), with vertex, bone and skeleton details. Both the current (Unity 2018+)
    and the older (Unity 5 to 2017) vertex layouts are read.
  - AnimationClip playback on the character (skinned in real time), with play/pause, scrubbing and loop.
    Skin clips that are override layers are combined with their base clip, as the game plays them.
- **Export** (multi-select with Ctrl/Shift+click, from the Export menu or the right-click menu)
  - Textures and sprites to PNG or DDS (BC1-5 kept in their original compression, with mip levels).
  - Audio to WAV.
  - Meshes to OBJ or COLLADA (DAE); skinned meshes include the named skeleton (from the SkinnedMeshRenderer
    or, for optimised rigs, from the Avatar) and the skin weights.
  - Character AnimationClips to COLLADA with the rig and meshes, keeping the original keyframes (BEZIER);
    layered skin clips export combined with their base clip.
  - **Raw**: data exactly as stored (serialised objects, streamed data, FMOD banks, TextAsset contents, bundle entries).
  - **Export All Known Formats**: everything under the selected nodes, with the formats chosen in
    Export > Preferences (mesh DAE/OBJ, images PNG/DDS, animation clips and sound on/off).

## Building

```
dotnet build -c Release
```

Requires the .NET 10 SDK on Windows. NuGet packages: BCnEncoder.Net, LZMA-SDK, NVorbis, Kyaru.Texture2DDecoder
(Crunch and ETC decoding).

## Not supported yet

Mobile texture formats ASTC/PVRTC/EAC, compressed meshes (m_MeshCompression), Unity 4 meshes, prop (non-skinned)
animations, humanoid (muscle) clips.

## Credits

`Resources/fsb_vorbis_setups.bin` holds FMOD's Vorbis setup headers, converted from
[vgmstream](https://github.com/vgmstream/vgmstream)'s `vorbis_codebooks_fsb.h`, which were extracted by
[python-fsb5](https://github.com/HearthSim/python-fsb5) (HearthSim).
