# Third-party sources

`AdvLoop.cs`, `AdvPlayback.cs`, `AdvProgram.cs`, `AdvTweens.cs`, and `AdvAudio.cs` port portions of ournotes-player 0.1.6 `engine/loop.js`, `engine/tween.js`, `engine/audio.js`, `story/player-core.js`, and `story/commands/{misc,sound,choice}.js` to native C#. Command numbering is also checked against nnnotes `advcommand.py`. These additions follow the project's AGPL-3.0-only license. Reference JavaScript is not executed by the CLI.

`AdvCamera.cs` ports the matrix/camera conventions of `engine/math.js`, `story/field.js`, and the Tilt/Role handlers of `story/commands/camera-rotate.js` under the same license.

Animation sampling, expression/controller behavior and physics in `Actor.cs` and `PhysicsRig.cs` are adapted from `ournotes-player` 0.1.6, Copyright (C) the ournotes-player contributors, licensed AGPL-3.0-only. The full license is in `LICENSE`. Original source: https://github.com/empty-sekai/ournotes-player (`src/live2d/`). This native derivative does not use the browser-only linking exception. JavaScript packages and node_modules are not build or runtime dependencies.

The C# `Archiving` implementation ports the original project's `millsage-story-archive` acquisition, indexing and Director Pack behavior. It reads the community site's manifest formats directly using the .NET base class library. Native AOT compiler/runtime packages are supplied by the .NET SDK; the published CLI does not require a separate .NET installation.

The director uses seeded 30 Hz animation and direct mesh updates. Native game rendering includes the exported Lit-URP shader arithmetic, fade-motion data, expression blending and a two-frame display queue. Full Unity lifecycle parity remains under comparison.

Live2D Cubism Core is an externally loaded, proprietary SDK binary supplied locally by the user. Its license is in `CubismSdkForNative-5-r.5/Core/LICENSE.md`; it is excluded from our build output. Game assets are not part of this tool's source or license.

Native voice analysis loads Cubism MotionSync CRI 5.0.4 from the official `CubismSdkMotionSyncPluginForNative-5-r.1` package, stored locally beside the rendering SDK. Its license is in that package's `Core/LICENSE.md` and `Core/CRI/LICENSE.md`. The binary is excluded from the build output. Mouth presentation and phoneme postprocessing are adapted from ournotes-player's `live2d/lipsync.js` and `live2d/motionsync.js` under AGPL-3.0-only.

`CriLips.cs` and `CriLipsDsp.cs` port the openY analysis path of ournotes-player `live2d/crilips/` under AGPL-3.0-only. Neural network weights and behavior constants are read from the existing archive; they are not embedded in the source. Platform `MathF`/`Math` elementary functions replace the reference's ARM libm reproduction, so last-bit differences are possible. `tests/OurNotes.Director.Tests/Fixtures/crilips-voice.json` contains reference openY values for the two voice cues named in `CriLipsTests.cs`, sampled every 1600 PCM samples with one second of silence after each cue.

API references used for implementation:

- https://docs.live2d.com/en/cubism-sdk-manual/cubism-core-api-reference/
- https://docs.live2d.com/en/cubism-sdk-manual/ow-sdk-mask-premake/
- https://learn.microsoft.com/en-us/windows/win32/opengl/wgl-and-windows-reference
