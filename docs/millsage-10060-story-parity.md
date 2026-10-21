# Millsage 10060: original character and frame effects

This regression uses the original `intl/10060` episode, its language UI,
prefabs, textures, particle systems, controllers and Cubism models. The native
story samples use `AdvStoryRenderer` with `script: null`, 960 × 540 output,
one sample per pixel, and the original 30 Hz loop.

## Kanata's anonymous entrance

The model is present in the archive:
`adv_live2d_sub_kanata_casual_spring_01`. Original Character row 2 binds it to
`kanata`. The `???` speaker name is intentional: Talk row 437 has
`TargetStatus: 1`, which resolves the original unknown-name text entry 11000.

The relevant original command sequence is:

| Row | Command | Character | Slot | Observed first execution frame |
| --- | --- | --- | --- | --- |
| 370 | In | hotaru | 5 | 14456 |
| 386 | In | hotaru | 1 | 15071 |
| 398 | In | hotaru | 9 | 15147 |
| 435 | In | kanata | 5 | 16028 (08:54.267) |

There is no intervening Out between those Hotaru moves. The old native renderer
retained three slot entries pointing to the same moved model. It rejected
Kanata's In because slot 5 still had an entry, although Hotaru's actual parent
was slot 9. The active stage map now keeps one entry for each moved model and
tests occupancy against the actual native transform parents. Historical
character-name/position mappings used by scripted commands remain intact.

Reference: upstream
[`AdvCharacterField.existsCharacter`](https://github.com/empty-sekai/ournotes-player/blob/a18de5b5e0b3e1fb3fe185008154056f3e7cbce3/src/story/field.js)
tests the slot's actual character children, and
[`In`](https://github.com/empty-sekai/ournotes-player/blob/a18de5b5e0b3e1fb3fe185008154056f3e7cbce3/src/story/commands/character.js)
reparents the existing model. The upstream direct renderer also iterates each
model once; its field-renderer slot bookkeeping is not itself the occupancy
test.

The native regression checks the original pre-entry slot state, model identity,
actual transform parent, evaluated visible geometry, original motion and the
anonymous speaker label. It also hides Kanata only for one same-frame diagnostic
readback, verifies that her model contributed visible pixels, restores her and
requires byte-identical original pixels. The shipped performance is never
replaced by a synthetic character sequence.

The same native renderer then runs the remaining original episode to completion
and checks all seven Kanata entrances, moves and re-entries, including her later
slot 5 → 1 → 7 sequence. This is a command/slot-state regression through the
complete story, not a claim that every frame has an independent visual oracle.

## Underwater frame's missing bubbles

The original `adv_frame_underwater/EfSplash` is a plain Transform, with no
RectTransform. Its serialized local XY is approximately zero and its scale is
approximately 108. Its two children, `Efbubbles` and `EfbubblesMini`, are
RectTransforms with the original Coffee UIParticle components.

The old canvas code laid out the plain Transform as a rectangle anchored at
(0, 0). That subtracted half of the parent canvas from its origin. Both bubble
systems were displaced together. A plain Transform now retains its local XYZ,
rotation and scale and exposes a zero-size parent rect to its RectTransform
children, matching upstream
[`TransformNode.layoutIn`](https://github.com/empty-sekai/ournotes-player/blob/a18de5b5e0b3e1fb3fe185008154056f3e7cbce3/src/story/features/canvas.js).
Programmatically created text/layout rectangles explicitly declare a rect.
The source EfSplash has no local-position animation binding. The pinned upstream
canvas animator also rejects `m_LocalPosition.x/y`; this change adds no new
position-animation semantics beyond the verified original layout contract.

The reference probe uses the original Frame row 249, original UI and prefab,
upstream PlayerLoop at 30 Hz, 120 steps, and a 960 × 443 story viewport. The
original ClampedCanvasScaler yields a canvas of approximately
960.1666603881731 × 443.07690682495905. Reference canvas origins are:

| Node | X | Y |
| --- | ---: | ---: |
| EfSplash | 480.08331874999476 | 221.53843815369046 |
| Efbubbles | 418.5233282199806 | -750.461424517208 |
| EfbubblesMini | 418.5233282199806 | -750.461424517208 |

The native tests check both the original hierarchy and the actual particle
hosts during the full original story at frame 10000. Particle spawn data,
curves, animation, materials, alpha, and intentional post effects are preserved.

## Reproduce

The native entry point is:

```sh
OurNotes.Director.NativeTests --millsage-story-parity FIXTURE OUTPUT_DIRECTORY
```

It writes `millsage-story-parity-report.json` and original-story PNGs at frames
1000 (kirakira), 10000 (underwater), 16058 (Kanata's entrance), and 16508 (later
anonymous dialogue). Reports retain original command rows, active frame/particle
state, native canvas origins, pixel hashes and the source manifest hash. The
manifest must remain byte-identical after the test.

The independent upstream reference is development-only and uses a pinned
checkout. It is not a runtime dependency:

```sh
git clone https://github.com/empty-sekai/ournotes-player UPSTREAM_CHECKOUT
git -C UPSTREAM_CHECKOUT checkout a18de5b5e0b3e1fb3fe185008154056f3e7cbce3
node scripts/millsage-frame-reference.mjs UPSTREAM_CHECKOUT FIXTURE OUTPUT_JSON
```

The script reads the original manifest, compressed assets and UI multipart
entries directly. It uses upstream's own `screenResources` test adapter to map
legacy serialized references to the current object API, without changing
positions, curves or particles. It runs the real upstream Frame, Animator,
canvas and particle modules and saves their complete geometry. It performs no
GL rendering: the reference proves layout and subsystem behavior, not complete
browser/native story pixel identity.
