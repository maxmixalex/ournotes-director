# Spine C# runtime

Source: <https://github.com/EsotericSoftware/spine-runtimes/tree/e7dc1435fa4a0083ab431f1b28e083c14a1f5c68/spine-csharp/src>

Pinned upstream commit: `e7dc1435fa4a0083ab431f1b28e083c14a1f5c68` (Spine 4.2).
This runtime reads Spine 4.2.x skeleton data. It is compiled into the native
OurNotes Director executable; no Unity, browser, or .NET installation is required
to play or export the animation.

The C# sources retain the upstream copyright headers. In `SkeletonBinary.cs`,
three `Enum.GetValues(Type)` calls use their generic overloads for Native AOT,
and three fixed-size stream reads use `ReadExactly` so truncated binary data
cannot silently reuse stale buffer bytes. No animation algorithms are changed.
The separate project adapts the build to .NET 10 and disables nullable
annotations and implicit usings for the upstream sources.
`LICENSE.txt` is the upstream Spine Runtimes License
Agreement and is copied beside every build and published executable.

The runtime is not licensed under the OurNotes Director project license. Its
integration and redistribution are governed by `LICENSE.txt` and the Spine
Editor License Agreement referenced there. In particular, the runtime license
requires each user to obtain a Spine Editor license unless the applicable
Spine Editor License Agreement grants distribution rights.
