# Third-party sources

`AdvLoop.cs`, `AdvPlayback.cs`, `AdvProgram.cs`, `AdvTweens.cs`, and `AdvAudio.cs` port portions of ournotes-player 0.1.6 `engine/loop.js`, `engine/tween.js`, `engine/audio.js`, `story/player-core.js`, and `story/commands/{misc,sound,choice}.js` to native C#. Command numbering is also checked against nnnotes `advcommand.py`. These additions follow the project's AGPL-3.0-only license. Reference JavaScript is not executed by the CLI.

`AdvCamera.cs` ports the matrix/camera conventions of `engine/math.js`, `story/field.js`, and the Tilt/Role handlers of `story/commands/camera-rotate.js` under the same license.

Animation sampling, expression/controller behavior and physics in `Actor.cs` and `PhysicsRig.cs` are adapted from `ournotes-player` 0.1.6, Copyright (C) the ournotes-player contributors, licensed AGPL-3.0-only. The full license is in `LICENSE`. Original source: https://github.com/empty-sekai/ournotes-player (`src/live2d/`). This native derivative does not use the browser-only linking exception. JavaScript packages and node_modules are not build or runtime dependencies.

The C# `Archiving` implementation ports the original project's `millsage-story-archive` acquisition, indexing and Director Pack behavior. It reads the community site's manifest formats directly using the .NET base class library. Native AOT compiler/runtime packages are supplied by the .NET SDK; the published CLI does not require a separate .NET installation.

The director uses seeded 30 Hz animation and direct mesh updates. Native game rendering includes the exported Lit-URP shader arithmetic, fade-motion data, expression blending and a two-frame display queue. Full Unity lifecycle parity remains under comparison.

Release packages include the unmodified target-platform Live2D Cubism Core runtime from `CubismSdkForNative-5-r.5` in `native/`. The release staging script downloads the fixed official SDK URL in `scripts/native-sources.lock.json`, verifies the archive size and SHA-256, checks each runtime and notice hash, and copies the SDK's Core license and redistributable-file notice to `licenses/live2d/`. It does not require a locally supplied or repository-tracked SDK archive. Live2D's proprietary runtime remains subject to the [Live2D Proprietary Software License Agreement](https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html), including its applicable publication terms; it is not relicensed under this repository's AGPL. Only the runtime required by the application is packaged, not the complete SDK, sample models or game assets. Game assets are not part of this tool's source or license.

Windows release packages also include the unmodified Cubism MotionSync CRI 5.0.4 runtime from the official `CubismSdkMotionSyncPluginForNative-5-r.1` package. CI downloads the exact version, verifies SHA-256, and copies the Windows x64 redistributable DLL plus its license/redistribution notices. Runtime lookup uses the application directory, with `CUBISM_MOTIONSYNC_DLL` available as an explicit override. Live2D does not supply a Linux desktop MotionSync runtime; Linux uses the C# CRI Lips implementation below, not the Android `.so`. See the [official platform support table](https://docs.live2d.com/en/cubism-sdk-manual/motionsync-plugin-platform/). Mouth presentation and phoneme postprocessing are adapted from ournotes-player's `live2d/lipsync.js` and `live2d/motionsync.js` under AGPL-3.0-only.

`CriLips.cs` and `CriLipsDsp.cs` port the openY analysis path of ournotes-player `live2d/crilips/` under AGPL-3.0-only. Neural network weights and behavior constants are read from the existing archive; they are not embedded in the source. Platform `MathF`/`Math` elementary functions replace the reference's ARM libm reproduction, so last-bit differences are possible. `tests/OurNotes.Director.Tests/Fixtures/crilips-voice.json` contains reference openY values for the two voice cues named in `CriLipsTests.cs`, sampled every 1600 PCM samples with one second of silence after each cue.

## Home rendering and Spine runtime

The native `Home*` implementation ports home camera, room/material and enabled HomeView volume behavior from [ournotes-player commit `9c1033a060c5dc5c169cedab6627b1b6b932c830`](https://github.com/empty-sekai/ournotes-player/tree/9c1033a060c5dc5c169cedab6627b1b6b932c830/src/story/simple/home) under AGPL-3.0-only. Resource and prefab formats are checked against [nnnotes commit `d34b9c7fae623b4528169f53cdfe373009f76e32`](https://github.com/MetaSekaiLab/nnnotes/tree/d34b9c7fae623b4528169f53cdfe373009f76e32/src/nnnotes), particularly `spot.py`, `room.py`, and `storyhost.py`. The CLI reads the original archived assets; reference JavaScript and Unity are not runtime dependencies. Home animation rendering does not use the Cubism runtime.

`third_party/Spine.Runtime` contains the Esoteric Software Spine C# runtime, pinned to [commit `e7dc1435fa4a0083ab431f1b28e083c14a1f5c68`](https://github.com/EsotericSoftware/spine-runtimes/tree/e7dc1435fa4a0083ab431f1b28e083c14a1f5c68/spine-csharp/src), for Spine 4.2 data. It is compiled into the Native AOT executable. The upstream copyright headers are retained; the small Native AOT and truncated-read adaptations are recorded in [ORIGIN.md](third_party/Spine.Runtime/ORIGIN.md).

This runtime is governed by its own [Spine Runtimes License Agreement](third_party/Spine.Runtime/LICENSE.txt) and the referenced Spine Editor License Agreement; it is not relicensed under this repository's AGPL. The runtime license requires each user to obtain a Spine Editor license unless the applicable Spine Editor License Agreement grants the relevant integration/distribution rights. Release packages include `licenses/Spine-Runtimes.txt` and `licenses/Spine-Runtimes-Origin.md`. Game skeletons, atlases, textures, and room assets remain separate from the tool's source and license.

## Bundled media programs

`tools/ffmpeg` and `tools/ffprobe` (with `.exe` on Windows) are separate executables built from FFmpeg 8.0.3, x264 commit `31e19f92f00c7003fa115047ce50978bc98c3a0d`, and zlib 1.3.1. They are invoked through process pipes/files, not linked into advkit or the proprietary Live2D runtime. The FFmpeg configuration enables GPL code and libx264; these media binaries are **GPL**, not an LGPL-only FFmpeg build. zlib retains its own permissive license. There are no nonfree FFmpeg components.

The release includes their licenses in `licenses/ffmpeg/` and the complete, unmodified source archives, source checksums, exact build script and generated FFmpeg configuration in `media-sources/`. Source URLs and hashes are pinned in `scripts/media-sources.lock`; binary/file hashes are written to `media-dependencies.json` and `media-files.sha256`. The media tools link x264/zlib statically and use operating-system C libraries. They provide local file/pipe processing, the output codecs used by the director, and FFmpeg's built-in decoders; hardware acceleration, device capture and network protocols are disabled. An explicit `--ffmpeg`/`--ffprobe` may select an alternate installation.

Transparent CineForm MOV uses FFmpeg's built-in `cfhd` encoder with `gbrap12le` and the `CFHD` tag. No Adobe or third-party system codec is invoked. The balanced default is the encoder's named `film1` quality setting. Adobe's CC 2014.1 announcement and 2015 Media Encoder manual document native CineForm MOV and RGB + alpha support; those sources and the limits of the project's tests are linked in [CINEFORM.md](CINEFORM.md). FFmpeg round-trip checks do not establish an actual Premiere Pro 2015.0 import result.

- FFmpeg source and licensing: https://ffmpeg.org/download.html and https://ffmpeg.org/legal.html
- x264 source: https://code.videolan.org/videolan/x264
- zlib source/license: https://zlib.net/

API references used for implementation:

- https://docs.live2d.com/en/cubism-sdk-manual/cubism-core-api-reference/
- https://docs.live2d.com/en/cubism-sdk-manual/ow-sdk-mask-premake/
- https://learn.microsoft.com/en-us/windows/win32/opengl/wgl-and-windows-reference
