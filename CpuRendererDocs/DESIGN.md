# CPU Renderer — Design

A tile-binned software rasterizer for Unity 6, using Jobs + Burst. Unity Editor is the level editor;
the renderer consumes meshes/transforms from the loaded scene. Stylized, Quake-inspired, deliberately
feature-light.

**Status:** design only, nothing built.
**Target:** portfolio tech demo — a few rooms, flythrough camera, imported Quake-style level kit.
Should be extensible toward a real game later, but that is not the current goal.

---

## 1. Guiding principle

Pick algorithms that fit the hardware instead of emulating a GPU. The CPU's advantages are cache
locality, cheap branch granularity, no barriers, and full control over the pipeline. The renderer's
constraints are the art direction, not obstacles to hide.

**Build the simple version first, measure, then optimize.** Several decisions below are marked
*benchmark* rather than settled. The point of the architecture is to make those swaps contained.

---

## 2. Aesthetic constraints (deliberate)

*Art-first* = keep it even if performance were free. *Tech-first* = drop it if performance allowed.

| Constraint | Kind | Notes |
|---|---|---|
| Low internal resolution, integer upscale, point filter | Art-first | Primary look knob, and also what makes the whole project feasible. A setting read at startup, default **640×360** (×3 = 1080p); e.g. **384×216** (×5) as an alternative. No runtime changes. |
| 32-bit color framebuffer | Tech-first | Not an 8-bit indexed pipeline. No HDR headroom — bright lights clip. |
| Colormap-style light ramp | Art-first | Light luminance quantized to N steps, a per-level tint shifts hue as it darkens; light colour rides on top (§4). This is the Quake color identity, not the lightmaps. Also hides lighting precision artifacts. |
| Nearest-neighbour texture sampling | Art-first | With mips — required, or it shimmers into noise. Level *selection* is dithered so the transition dissolves instead of drawing a line, and every pixel on screen is still one unfiltered texel — see §3. |
| Faceted (per-triangle) normals everywhere | Art-first | From the edge cross product, never from a vertex attribute. Art must be authored expecting facets: no smooth cylinders, pipes, or barrels. Period-correct for a Quake kit. Imported vertex normals are a clean later addition if smooth props are ever wanted — see §9. |
| Lambert only, no PBR | Art-first | **No specular initially.** Kept as a clean extension point on `LitTextured` — see §6. |
| Fullbright surfaces | Art-first | Cheapest emissive; lava, screens, lamps. |
| Sparse-keyframe vertex animation | Art-first | 10 Hz baked, lerped. The "floatiness" is linear interpolation between sparse poses, not jitter. Keyframe rate is a per-clip import setting. Forecloses procedural animation (IK, ragdoll) — irrelevant for a flythrough. |
| No PS1-style vertex snapping | Art-first | Subpixel-accurate raster, and perspective-correct rather than affine mapping — both halves of the PS1 look are rejected. Quake heritage, not PS1. |

Power-of-two textures and the resolution cap are pipeline constraints, not aesthetic ones — see §7.

### Reference looks

Quake 1's software renderer is the **main target**. The rest are the range an artist should be able to
approximate within the constraints above — *similar looks, not technical matches*; the lightmapped
entries are claimed for their palette and mood, not their lighting method.

**Quake 1** (main target) · Quake 2 · Unreal 1 · Half-Life 1 · Duke Nukem 3D · Dusk · Descent 2

A candidate feature earns its place by serving one of these. One that serves none is dropped.

---

## 3. Architecture

### Frame pipeline

```
Frustum cull (objects) ─> Animate ─> Transform verts ─> Backface + off-screen cull + Near clip + Compact ─> Project + Bbox + TriSetup ─> Bin ─┐
Cull lights + shadow blobs (screen rects) + entity shadow rays ───────────────────────────────────────────────────────────────────────────────┤
                                                                                                                                          v
                                                                                                    [IJobParallelFor over tiles] ─> Display
```

Ordering rationale — everything cheap that can reject a triangle happens before the per-triangle work:

- **Animate** only touches visible vertex-animated props: lerp local-space vertices between the two
  current keyframes, then recompute each triangle's face normal by cross product. From there they
  take the rigid-prop path. Their frustum-cull bounds must enclose every pose of the clip.

- **Backface culling needs no projection.** Each triangle's face normal is stored in its mesh's local space, and
  the camera is brought into each object's local space once per frame, so `dot(faceNormal, vertex - localCamPos) > 0` is one dot product on data requiring no per-frame transform. It rejects ~half the triangles before clip, projection, setup, and binning. (Equivalent to the screen-space signed-area test under perspective.) Transforming the camera instead of the normal stays correct under mirroring, because which side of a plane the camera is on survives any transform.
- **Two-sided surfaces** skip that test. Instead, when the camera is behind the face, the stage negates
  the normal. That is the whole two-sided feature: setup is orientation-agnostic (Rasterizer details),
  so nothing downstream knows a triangle was two-sided.
- **Off-screen triangles are rejected before clipping.** If all three clip-space vertices lie beyond the same screen edge (`x > w` for all three, or `x < -w`, `y > w`, `y < -w`), no pixel can land on screen. Three vertices are enough because a triangle stays inside any half-space that holds its corners, and that holds for `w < 0` too, so the test runs ahead of the near clip with no divide. It tests the screen rather than the guard band: it asks whether anything is visible, while the guard band asks whether coordinates fit. A triangle that wraps a screen corner passes the test and is handled by the bbox and edge tests, which is correct and rare.
- **The stage outputs each survivor's world-space shading normal** — the local normal
  carried by the inverse-transpose of the object's 3×3 matrix and normalized. After culling and the
  two-sided flip, every shading normal faces the camera.
- **Transform every vertex of each frustum-visible object**, once per vertex rather than per triangle
  corner. Backface culling does not reduce this; a vertex-liveness pass would, but only if a profile
  asks for it.
- **Compact survivors** into a dense array so setup and binning don't re-test liveness or waste SIMD
  lanes on dead entries. Backface cull, off-screen reject, near clip and compaction are one stage, written with the same
  count → offsets → scatter helper as binning (each chunk counts its 0–6 output triangles, then writes
  into its reserved range) — deterministic, no atomic append.
- Project, bbox and setup are one job. Projection (divide by `w`, convert to fixed point) is only
  ever consumed by setup, and bbox is logically independent of it — a single `IJobParallelFor` over
  30k triangles already saturates every worker, so splitting them only adds a stage boundary and
  costs locality.
- **Lights and blobs are not binned.** Their cull stage computes each one's screen rect; each tile
  job then gathers its own (below). See Lighting (§4) for why.

### Per-tile job (the core)

Baseline is **single pass**: rasterize, depth-test, shade what passes.

```
0. Gather lights + blobs whose screen rect overlaps the tile   -> tile-local lists
1. Raster + depth test + shade, in bin order                   -> color + depth
2. Raster transparent, depth-test only                         -> per-pixel fragment lists   [optional]
3. Per pixel: sort back-to-front, blend over                   -> color                      [optional]
```

Steps 2–3 are **Pixel Sorted Transparency** (§5), the one optional expansion the renderer ever
grows. Every surface type in the baseline writes depth, so the opaque pass needs no sub-ordering of
any kind.

**Tile is the unit of parallelism, and one thread owns every pixel in its tile.** That ownership
invariant is the load-bearing decision — it is what gives no framebuffer atomics, deterministic
output, and linear core scaling. It is a property of the *partitioning*, not of the memory layout;
the two are independent and are often conflated.

### Overdraw

No overdraw optimization up front: opaque triangles are shaded in bin order, and every pixel that
passes the depth test is shaded. If overdraw shows up in the profile, the escalation order is:

1. **Front-to-back sort** of the opaque part of the tile's list, by max `1/w` (nearest point),
   descending. A few lines inside the tile job; later triangles then fail the depth test early.
2. **Depth prepass** — rasterize depth only, then re-rasterize to shade. Bounds shading to once per
   pixel but pays a second per-pixel raster pass. Only if the sort is not enough. See §9.

### Tile size

**32×32 is the default and the reference to compare against.**

- At 8 bytes/pixel (color + depth), 32×32 is **8 KB** — fits L1D with room left for the texture
  working set.
- 64×64 is **32 KB** — all of a 32 KB L1D (Zen 2–4) or two thirds of a 48 KB one (Golden Cove,
  Zen 5), leaving little or nothing for texels. Not a coin flip — it is the suspect option.
- 16×16 is 2 KB but amortizes per-tile overhead badly (list headers, job dispatch, per-tile
  re-derivation of edge equations).
- **Tile count is also parallelism granularity.** 640×360 gives 240 tiles at 32×32 versus 60 at
  64×64. On 16 cores that is 15 tiles per core versus under 4 — two heavy tiles become unrecoverable load imbalance at 64.

Benchmark 16 / 32 / 64. If the answer turns out to depend on core count or resolution, select **once
at initialization** from thread count and framebuffer size. Never switch dynamically. Include a low
resolution in the benchmark: 384×216 gives only 84 tiles at 32×32, the same imbalance risk as 64×64
at 640×360.

Neither resolution divides evenly (360/32 = 11.25, 216/32 = 6.75). **Pad the framebuffer to a multiple of the tile size and crop at display** rather than special-casing partial tiles.
Then widen each row by one more cache line (16 px, 64 B): 640 → 656. At an exact 2560 B pitch a
tile's 32 rows map onto only 16 of the 64 L1 sets, and color + depth together can fill them; an odd
line count per row spreads them across all sets. Also cropped at display, so it costs nothing.

### Binning

Output is **one flat contiguous array of triangle indices, plus a `(start, count)` per tile.** Each
tile job then streams a contiguous run — that layout is the whole point.

Built without atomics in three steps:

1. **Count.** Triangles are split into fixed chunks (`chunk = triIndex / CHUNK_SIZE`). Each chunk
   counts how many of its triangles touch each tile, into its own row of a `numChunks × numTiles`
   int array (e.g. 64 × 240 × 4B = 60 KB, no allocation, no contention). Rows are keyed by chunk,
   never by worker thread: `IJobParallelFor` hands batches to whichever worker is free, so a
   thread-keyed row would be counted and scattered by different triangle sets.
2. **Turn counts into offsets.** Walk that table once, single-threaded, **tile-major** (for each tile,
   for each chunk), keeping a running total: the first bucket starts at 0, the next starts after it,
   and so on. Tile-major order is what makes each tile's list contiguous. Each `(chunk, tile)` pair
   ends up with the exact slot range it owns. 15360 additions.
3. **Scatter.** Re-run the tile-range loop per chunk, writing each triangle index into its chunk's
   reserved slot.

Deterministic, zero atomics, zero per-frame allocation, exact sizing. Pass 3 reuses the bbox computed at setup, so it is only a rect loop.
This is one shared helper, used for cull/clip compaction and triangle binning.

- **Bin triangle indices, not setup data.** Setup lives once in a flat array. A screen-filling floor
  triangle costs 240 × 4 bytes across the tile lists, not 240 copies of an edge-equation struct.
- **Atomic append is rejected**, not an alternative: it varies intra-tile ordering between runs,
  which breaks the determinism §7 and §10 depend on.
- Two-level coarse/fine binning only matters far past demo scale. Not now.

### Framebuffer layout

Baseline: **one linear full-screen color buffer and one linear full-screen depth buffer**, with each
tile job scissored to its rect and writing in place. No scratch, no swizzle, no copy-out. Needs
`[NativeDisableParallelForRestriction]`, which is standard for this pattern; safety checks compile out entirely in a build, so they cost nothing at runtime.

A tile row is 128 contiguous bytes (2 cache lines) per buffer and a tile touches 32 such runs — the
same 4 KB per buffer (8 KB for color + depth) as a private buffer, only strided. **Whether tile-major private buffers plus a copy-out
beat this is unproven; benchmark it against the linear baseline.** Neither affects the ownership
invariant.

Note that in the baseline there is **no CPU post pass** — the ramp is applied in the surface loop and
the upscale happens in the display. The tile-boundary rule below is therefore forward-looking.

### Rasterizer details

- **Fixed-point edge equations, top-left fill rule.** Fixed point is chosen over float for *exact*
  arithmetic: the same edge test returns a bit-identical answer wherever it is evaluated, so a shared edge never cracks or double-shades — provided both triangles were handed the same vertex, which is what the clip rule below guarantees. Subpixel precision, guard-band size, and integer width are a joint constraint, pinned together: **4 subpixel bits, `G = 2`, int32 throughout**, with no int64 setup. Edge values are bounded by `2·G²·W·H·s²` (`s = 16`), which is 471,859,200 at 640×360, under int32's 2,147,483,647. Changing any of the three, or the render resolution, means rechecking this bound.
- **Near-plane clipping**; guard band + scissor for the four sides. Near is the one plane that cannot
  be deferred: as `w→0` the projected coordinates explode past any usable coordinate range. Near clipping alone outputs at most 2 triangles; with the
  side-plane fallback below, at most 6. A clipped polygon holds at most 8 vertices, which fan into those 6 triangles. **There is no far plane** — `1/w` depth tends to 0 at infinity, which is perfectly representable, so nothing needs clipping or a depth range.
  - **Intersections are computed from the inside vertex outward** — `t = (near − w_in)/(w_out − w_in)`,
    then `v_in + t·(v_out − v_in)` — never in polygon-walk order. Which side of a plane a vertex lies on
    is a property of the vertex, so two triangles sharing a cut edge agree on which endpoint is inside
    and evaluate the identical expression, giving a bit-identical new vertex. A Sutherland-Hodgman loop's
    natural `prev + t·(cur − prev)` traverses that shared edge in opposite directions in the two
    triangles; the results can snap to different subpixel points, and the fill rule then drops the pixels
    between them — a line of background along the near plane. Same rule for the four side planes.
- The four side planes need no clipping for **coverage** — out-of-range pixels are scissored away —
  but they do for **coordinate range**. After near-clipping, `x/w` is still unbounded: a large floor
  plane at a grazing angle overflows the fixed-point range regardless of guard-band size. **Rare-path fallback:** if any vertex lies outside the guard band — tested in clip space as
  `|x| > G·w` or `|y| > G·w`, since this runs in the cull/clip stage before projection — clip against
  the four side planes in homogeneous space. A handful of triangles per frame, and it removes the whole "the floor
  disappeared" bug class.
- Evaluate edges 4 pixels at a time with `float4`/`int4` from `Unity.Mathematics`. Depth test is a mask, not a branch.
  8-wide needs Burst's `v256` AVX2 intrinsics plus a 4-wide fallback — an optional later path.
- **One normal per triangle, from the cross product of two edges** in the mesh's own local winding —
  computed once per mesh when it enters the mesh library (local space, §7),
  recomputed each frame for vertex-animated props after the lerp. It serves both backface culling and shading, so the two
  can never disagree: a mis-wound triangle is culled and renders as a *hole*, never as a visible face
  lit from the wrong side. There is no normal vertex attribute and no normal interpolator.
- **Orientation-agnostic setup.** Setup already computes the triangle's signed screen area for the
  attribute gradients. Zero → reject; negative → swap vertices 1 and 2 (with their attributes). The
  rasterizer then only ever sees one orientation, so neither mirrored transforms nor two-sided faces
  seen from behind need a flag. The fill rule stays watertight: two triangles sharing a screen edge
  lie on opposite sides of it, so after the swap they cross it in opposite directions and exactly one
  owns it. A near-edge-on sliver where the world-space cull and the screen area disagree by rounding
  renders as a sliver instead of nothing — harmless.
- **Interpolants: `1/w`, `u/w`, `v/w`.** World position for attenuation is *reconstructed* from the screen ray and depth -
  `worldPos = camPos + rayDir(x,y) * w` - where `rayDir` steps linearly across the tile.
  - **`w` is view-space depth** (clip w: distance along the camera's forward axis, not along the ray), and
    `rayDir(x,y)` is the ray through the pixel scaled so its forward component is 1. That scaling is why it
    steps linearly and why `camPos + rayDir·w` lands on the surface — never normalize it.
  - `w = 1/(1/w)` is the reciprocal already computed for `u = (u/w)·w`, so the reconstruction adds no division.
  - **The real win is amortization across lights.** `lightVec = (lightPos - camPos) - rayDir·w`, and the first term is constant per light per frame — so `rayDir·w` is computed once per pixel and reused for every light in the tile, leaving one subtract per light.
  - Reconstruction error scales with `w`, but linear falloff with a hard cutoff makes distant error
    irrelevant by construction.
- **Depth buffer: float32 storing `1/w`.** Linear in screen space, so it interpolates directly. This
  is exactly the reverse-Z distribution, so float32 precision is a non-issue.
- **Mip selection: exact λ per pixel, dithered floor.** Per-*triangle* mip produces a hard seam down
  the middle of a two-triangle wall; flooring λ produces a hard contour. Per-pixel λ removes the
  first, dithering the second.
  - **λ per pixel:** `λ = log2(max(ρx, ρy))`, where `ρ` is texels per pixel along each screen axis.
    Both axes and `max` — a wall at a vertical grazing angle has `ρy >> ρx`, so taking `ρx` alone
    under-blurs it straight back into shimmer. The gradients come from values the loop already has:
    `du/dx = w·(A − u·B)`, with `A = d(u/w)/dx` and `B = d(1/w)/dx` constant per triangle from setup;
    likewise for `v` and for `y`. `u` and `v` are in texels, so non-square textures pick the right
    level. Two coplanar triangles get identical gradients, so no seam, and λ depends only on screen
    position, so it is tile-safe.
  - **Cheap, robust `log2`:** use squared lengths, `λ = 0.5·log2(max(ρx², ρy²))`, so there is no
    square root, and `log2(x) ≈ float(asint(x))·2⁻²³ − 127` — exponent plus `(mantissa − 1)`, one
    convert and one FMA. The dither needs that fractional part. It reads at most 0.086 levels low,
    and it is continuous and monotonic, so it cannot band; mip bias absorbs the offset. `ρ = 0` gives
    −127 and `ρ = ∞` (near the horizon — there is no far plane) gives 128, and both land on a valid
    level after the clamp. No NaN path.
  - **Dither, then floor:** `mip = (int)(λ + dither(x, y))`, dither in `[0,1)`. Since `λ = N + f`,
    this lands on `N+1` with probability exactly `f` — the trilinear weight, delivered spatially
    instead of by blending. Clamp λ to `[0, maxLevel]` *before* the add and no second clamp is needed.

  This is **stochastic trilinear with the sampler still unfiltered**: every pixel is one raw texel
  from one level, no lerp anywhere. The hard contour becomes a dissolve whose density tracks λ
  continuously. By count, **~15 vectorizable ops per pixel**. If a profile flags it, compute λ
  exactly only at the ends of 8–16 pixel spans and interpolate — a performance-pass option, not the
  baseline.
- **Dither source: 4×4 ordered Bayer.** 16 bytes, a static array, no asset and no generator. Because
  the pattern repeats every 4 in `x`, precompute four SIMD vectors indexed by `y & 3` and the pixel
  loop never touches memory for it. 16 thresholds quantizes the mip fraction to 1/16 steps, which is
  more than enough. Ordered dithering is also the period-correct artifact: a regular pattern reads as
  a design choice, irregular noise reads as a bug. 4×4 over 8×8 because at ×3 upscale its cell is 12
  display pixels rather than 24, and the extra thresholds buy nothing.
  **Blue noise is a one-line table swap**, held in reserve for one specific failure: dithering is most
  active near 1:1 texel:pixel, so a 4×4 grid and the texture's own texel grid sit at comparable
  frequencies and can beat into moiré. Watch for that on the milestone 3 benchmark scene once it is
  textured (milestone 4).
- **Mip bias**, one tunable float added to λ before the clamp: positive trades shimmer for blur.
  Within-level aliasing is the main shimmer source — any pixel on level `N` while `λ > N` is
  undersampled by up to 2× — and the bias is the knob for it.

### Tile boundaries — what is and isn't safe

First tiled renderer, so the rules are worth stating explicitly.

| Operation | Status |
|---|---|
| Rasterizing a triangle spanning several tiles | **Safe.** Every tile evaluates the same edge equations at the same sample points and scissors to its own rect. Exact fixed-point arithmetic means the result cannot differ per tile. |
| Color / depth writes | **Safe.** One thread owns the tile's pixels; no overlap, no atomics. |
| Mip selection and attribute gradients | **Safe** — computed analytically from triangle setup, never by differencing neighbour pixels. This is why no `ddx/ddy` equivalent is needed. |
| Dither / noise patterns | **Safe only when indexed by absolute screen coordinate.** Tile-local indexing makes the tile grid visible. |
| Reading a displaced neighbour pixel (refraction, heat haze, blur) | **Unsafe.** The neighbour may live in another tile that is unshaded, or being written by another thread right now. |

**Rule: anything that needs a pixel it did not shade itself belongs in a post pass**, which would run
after all tile jobs complete and see the whole framebuffer. Watertightness between triangles (from the
fill rule) and tile independence are separate properties — don't conflate them.

### Cost model

**No frame-rate target and no frame cap.** The renderer runs as fast as it can, matching Unity's
default behaviour. What matters is relative cost, not a budget.

**No assumed bottleneck.** Build the simple version, profile it once the milestone 3 benchmark scene
exists, and only then add complexity where the profile points. The known facts:

- Per-triangle work (transform, clip, setup, binning) scales with triangle count and is paid whether
  or not the triangle ends up visible.
- Shading cost scales with shaded pixels × lights per tile; how many pixels are shaded depends on
  overdraw.
- The one unpredictable cost inside the shading loop is the **texture fetch** — the only random access
  in an otherwise cache-resident inner loop. Any estimate assuming L1 residency is optimistic.

---

## 4. Lighting

**Tiled forward (Forward+). Light colour and intensity stay live; only visibility is baked.**

The one term in the lighting equation that is genuinely static is **visibility** — what blocks what.
Baking that factor and nothing else leaves every other term live, which is what §7's live gather
requires. Baking irradiance instead would weld in intensity, colour and falloff along with it; that
distinction is the whole of §9's lightmap entry.

**Tiles gather their lights; lights are not binned.** The light cull stage computes each visible
light's screen rect from a perspective sphere projection; a sphere containing the camera or crossing
the near plane covers the full screen. At the start of each tile job, the tile tests every visible
light's rect against its own and keeps the overlaps, in index order, in a tile-local
`FixedList512Bytes<ushort>`. Each tile ends up with ~0–8 lights; the inner loop iterates that list.

- **Why gather, not bin:** binning (scatter) pays off when items are many — 30k triangles × 240
  tiles would be 7.2M tests. Dozens of lights × 240 tiles is ~12k rect tests of 4 compares each,
  microseconds spread across the workers. Gathering removes a stage, its buffer, and its count pass,
  and stays deterministic.
- **Cap: 255 visible lights and 255 visible blobs** — the list's capacity. `Debug.Assert` in the
  cull stage.
- Lambert × attenuation, **linear falloff with a hard cutoff radius** (Quake dlight style — also
  bounds each light's screen rect; the 2D rect test is still conservative, with no depth rejection).
- **Colormap ramp with coloured lights.** Light (ambient + Σ lights) is accumulated as RGB. Its
  luminance `Y` is quantized to a ramp level; the light's hue is kept separately as
  `lightChroma = rgb / Y`. Final color = `texel × tint[level] × lightChroma`, where `tint` is a small
  per-level RGB table carrying the quantized brightness and the hue shift (and optional overbright
  levels above 1.0). One table
  read and a few multiplies per pixel; guard `Y ≈ 0` with the lowest level.

### Baked per-light visibility

**A scalar per (static surface, static light), sampled per pixel and multiplied into that light's own
term before the ramp quantizes.** This is what stops light leaking: a wall blocks its light because
the bake traced a ray and hit the wall, not because someone authored a mask.

**Unity's Shadowmask bake produces it; we transcode it into our own format at bake time** (§7).
Measured viable — results in `Experiments/shadowmask-findings.md`. The **format is the design
decision and Unity is only the first producer**: a hand-written ray bake over the same `uv2` substrate
drops in behind the same interface, and §9 records what would justify one.

- **Costs a second interpolated UV set.** Texture UV from `uv1`, visibility UV from `uv2`,
  interpolated independently. Unlike Quake we cannot derive one from the other — arbitrary meshes have
  unrelated `uv1`, and a tiling `uv1` would break it outright.
- **Shadows are hard.** `Light.shadowRadius` is inert in a Shadowmask bake. Lightmap resolution is the only softness dial and it works *inversely*: coarser is softer. Wanting real penumbra is the one requirement that forces our own bake.
- **Four shadow-casting lights per texel.** A light past the limit renders unshadowed — a regression to the previous behaviour, not corrupt data. A light whose shadow does not matter is set to cast none and then costs no channel, so the real budget is four *shadowing* lights plus unlimited unshadowed fill. Tightening `range` to the actual cutoff radius is the other lever, and it cuts per-tile light counts at the same time.
- **A `0` in the mask means *either* "shadowed" *or* "no data".** A sample is only meaningful paired
  with the light that owns that channel and that light's range; channels are never combined.

**Format — per-(object, light) contiguous byte slices, not an atlas.** Unity packs atlases for GPU
draw-call reasons that do not apply to us. Contiguous-per-object means a surface being shaded touches
only its own bytes, where an atlas sub-rect strides by the full atlas width, and the sampler loses the
scale/offset multiply. The per-object record carries
`{ visTexelOffset, visWidth, visHeight, visLightFirst, visLightCount }`; slice *k* lives at
`visTexelOffset + k * visWidth * visHeight` and belongs to `visLights[visLightFirst + k]` (§7).
Roughly 1.6 MB for 100 objects at 64² with four lights each.

- **`visLightCount == 0` is the "sampled at runtime" branch** of the table under *Dynamic objects* —
  one field, not a second flag to keep in sync.
- **Light → slice resolves at triangle setup, not per pixel:** a small tile-light-slot → slice map
  built once per triangle, exactly where the area-mask test used to live. The pixel loop indexes it.
- **The transcode resolves Unity's ambiguities so they never reach the runtime.** A slice exists only
  if that light reaches that object, so every texel inside one is real data and the `0` above cannot
  be mistaken for "no data". This is the argument for transcoding at all, rather than sampling
  Unity's textures directly.
- **Bilinearly sampled** — Quake's own pairing of nearest-sampled textures with a smoothly
  interpolated lightmap, and the reason a coarse map reads as soft rather than blocky. §9's rejection of bilinear filtering is about *textures*, where magnification is the aesthetic; lighting is the opposite case. Costs 4 byte fetches and 3 lerps per light per pixel, roughly doubling the per-light pixel cost — flagged for §10, not pre-optimized.
- **One byte per texel, linear — not one bit.** Shadow edges carry genuine intermediate values (a
  measured `0.50` where the edge crosses a texel), and that half-texel is the only softness the
  format has.

**This reverses an earlier decision.** Area masks — `areaId` per surface plus a 64-bit mask per light — are gone. They were chosen deliberately, because *art-directed* reach beat portals' geometric
correctness: spill through a doorway is desirable. Real occlusion gives that spill the right *shape*
for free, so the trade is mostly a win, but one case is genuinely lost: "do not light that room even
though geometry says you should." If it is ever wanted it returns as a per-light exclusion list, not as
a mask on every surface.

### The sun

**One optional directional light, hoisted out of the tile list.** It has no position, so no screen
rect, no attenuation and no cutoff radius — gathered like the others it would land in every tile's
list and consume a slot everywhere for nothing. It is computed once per pixel beside ambient, which
needs the same face normal at the same point in the loop. **Cap of one, `Debug.Assert`.**

- **Baked-only, which deletes cascades, shadow distance, depth bias and cascade seams** — the entire hard part of realtime directional shadows.
- **The bake freezes its direction; colour and intensity stay live.** Same principle as a point light,
  where position is what freezes. So no time-of-day, but a sun that dims or spikes works — correctly shadowed lightning, for free.
- **Not usable unshadowed, so it is gated on the bake rather than independent of it.** A point light's hard cutoff confines its leak to roughly one room; a directional light has no falloff and reaches every surface in the level through every wall.
- Expected to cost one of the four channels everywhere, since its volume is unbounded. **Inferred, not measured** (§13).

### Ambient volumes

**Hemisphere ambient (sky + ground colour) from author-placed AABB volumes.** Art-direction control
rather than GI approximation, and the substitute for baked bounce light (§9).

- **Smallest containing volume wins**, tiebroken by lowest index so the choice is deterministic. A
  global default covers anything inside no volume.
- **Looked up per triangle, not per mesh.** A mesh spanning two rooms would otherwise take one answer for both — exactly the failure that made `areaId` inadequate.
- **Precomputed at bake for static geometry.** Neither the geometry nor the volumes move, so the answer never changes: one index per triangle, zero per-frame cost. Movers query once per frame at their origin.
- **No blending.** Boundaries land on triangle edges, and the ramp quantizes luminance, so an ambient step across a boundary either falls inside one level or shifts by one — indistinguishable from the banding the ramp already produces everywhere by design.
- **Known limitation: ambient is not occluded**, so an unlit room still receives its volume's ambient
  in full. Keep ambient contrast moderate between regions that can see each other.

### Dynamic objects

**Which of the two paths an object takes is declared by `ReceiveGI`, never inferred from its size.**
Unit scale is arbitrary — someone may build at 1u = 1cm — and "big" is not the real question anyway: a long thin pole is big in one axis and one sample suits it fine. The checkbox *is* the trade:

| `ReceiveGI.Lightmaps` ticked | Not ticked (default) |
|---|---|
| Real gradient across the surface | Flat across the whole object |
| Goes stale once it moves | Always current |
| Platforms, doors, large movers | Monsters, items, the player |

- **Lightmapped movers need no extra mechanism.** `StaticEditorFlags` is a bitmask, so `ContributeGI`
  without `BatchingStatic` uses it as intended; and because we read the mesh and apply its current
  transform ourselves, *static for lighting* and *static for transform* are already decoupled. The
  chart bakes at the rest position and rides along — which is what Quake did for `func_plat` and
  `func_door`, including the shadow left behind at the rest position.
- **Sampled objects get one shadow ray per light in range**, from the object's centre toward the light,
  traced against colliders and batched through Jobs. Blocked means that light is off for the whole
  object. This is Quake's `R_LightPoint` with the light direction kept.
- **A layer mask must exclude the object's own colliders**, or every object shadows itself.
- **The two occluder sets have to agree.** Unity's bake traces against *meshes*; these rays trace
  against *colliders*. Decorative geometry with no collider shadows the floor but not the monster
  walking under it, and an oversized collider does the reverse. **Shadow-casting geometry needs a
  collider matching its mesh.**

### Shadow blobs ground the props

**Blobs are ambient contact occlusion, not shadowing.** They ignore light direction, so they darken an object's footprint even where no light reaches it — which is precisely what a shadow ray cannot do, and why they survive alongside baked visibility instead of being replaced by it.

A prop gets a shadow sphere (optionally squashed) below it only
if it has a `BlobShadow` component, which holds the sphere's size and squash. Opt-in, so a level piece that was missed when setting the Static flag costs performance but never grows a stray shadow. Blobs are gathered live and collected per tile exactly like a light. In the lit surface loop, a pixel inside a blob has its accumulated light (ambient included) scaled down, fading with distance from its centre, using the reconstructed world position the loop already has. This happens **before** the ramp picks a level, so the shadow steps through ramp levels like all other lighting. Only upward-facing surfaces are affected, and a per-surface flag keeps a prop from darkening itself. No quad, so no z-fighting and no depth bias, and it wraps correctly over stairs and uneven floors.

---

## 5. Transparency

Most of the bug class (opaque-ish alpha textures overwritten, concave self-sorting, interpenetration, center-sorted large objects) comes from **conflating alpha test with alpha blend**. Separating them
removes it rather than working around it.

**`AlphaTested` is an opaque surface type.** Depth write, opaque shade, no sorting anywhere. The only difference from `LitTextured` is a discard on alpha.  Fences, grates, ladders, foliage.

Most of these are also **two-sided** — a fence quad seen from behind must not vanish. The per-surface `twoSided` flag is read in exactly one place, the cull stage (§3): skip the backface test, and negate the normal when the camera is behind the face. Setup's winding swap handles the raster side. Two lines, but nothing works without it.

### What the baseline supports

**Alpha test, and nothing else** — no blending, no sorting, no per-pixel lists. That covers the
demo. `Liquid` is opaque and warped, which is the software-Quake look anyway; translucent water
arrived with GLQuake.

There is exactly one way this grows, and it is optional — nothing else in the doc depends on it.

### Pixel Sorted Transparency — the one optional expansion

**Pixel Sorted Transparency** is real alpha blending, sorted per pixel within the tile. Referred to
by that name everywhere else in the doc. It is a pure addition — nothing already built has to
change — and the cost stays contained:

- **One thread owns the tile**, so per-pixel storage is a plain indexed array — no atomics, no
  allocation. This is the entire reason GPU OIT is hard and this isn't.
- **Shade at insertion time**, store final RGBA. The surface loops never learn about transparency;
  only the *output stage* differs (write to color buffer vs. insert into a slot).
- **Sorting is per pixel**, so no object-level sort exists to get wrong. That is what removes the bug
  class rather than merely avoiding it.

**Storage is K fixed slots per pixel in two planes** — `depth16[px*K + s]` and `RGBA8[px*K + s]`,
with **K = 3** the default. Insertion compares only depths, so the search touches a dense 2-byte
plane, 6 B per pixel in one load, and only the winning slot touches colour.

Two properties shrink this to the whole structure. **The under operator never reads depth** — it needs only α and C in order — so depth is scratch for sorting, not payload, and a fragment is 6 B rather than the 12 B a linked list spends on a `next` index. And **a zeroed slot is inert**: α = 0 leaves `A` and `T` untouched, and `depth16 = 0` is `1/w = 0`, infinitely far, which already sorts to the back.
Clearing the planes to zero is therefore the entire initialization — no per-pixel count, and
compositing is an unrolled K-step loop with no branch. Clear only when the tile has transparent
triangles binned.

Composite front-to-back with the *under* operator — `A += T·α·C`, `T *= (1−α)`, finishing
`A + T·C_opaque`. Capacity is per-pixel by construction, so there is no shared pool to exhaust and no tile-global failure mode.

**A fragment farther than all K slots merges into slot K−1 rather than being discarded:**
`α' = 1 − (1−α_tail)(1−α_new)`, colour weighted to match. That combine is exact for adjacent layers and slot K−1 holds the farthest by construction, so **transmittance stays exact at any depth complexity** — the opaque background always shows through by the right amount, which is the error that reads as "the glass got thinner." Only colour *ordering* inside the merged tail degrades, and only when two fragments both farther than slot K−2 arrive back-to-front; that error is second order in α and scaled by the smallest transmittance in the stack. Once the tail saturates (α > 254/255) later fragments are discarded on one compare.

K is a single dial with no structural change: 6 KB at K = 1 (keep-nearest), 18 KB at 3, 24 KB at 4 —
the last tight against a 32 KB L1D. Colour is cold during this pass, since opaque shading is finished
and compositing has not started, so the live set is depth plus planes plus transparent texels, not
the whole framebuffer. Settle K by eye on a scene with stacked glass.

---

## 6. Surface loops (our "shaders")

Hand-written Burst inner loops, one per surface type. No shader language, no permutations, no
abstraction layer.

**Certain:** `LitTextured` · `Fullbright` · `AlphaTested`
**Nice to have:** `Liquid` · `Particle`

- `Sky`: **not adding one.** Regular opaque `Fullbright` geometry — a distant dome — should
  probably achieve the same look with no renderer feature at all. Reconsider if that runs into
  trouble.
- `Liquid`: sine-warp of the texture lookup (Quake `turbsin`). Nearly free in software.
- `Particle`: opaque flat colour, depth-tested and depth-written — what software Quake's
  `D_DrawParticle` does. No blending, no sorting.
- **Specular** is the natural later extension to `LitTextured`. The view vector is already available
  from the reconstructed world position, so adding it costs no new interpolants. Faceted normals make highlights pop per-facet — a look decision, not a bug.

**Structure:** one function per surface type, selected by a `switch`. No interfaces, no generics, no
function pointers.

---

## 7. Unity integration

**Baked: mesh, texture, animation and visibility data. Live: per-frame scene state.**

The invariant that matters is not "the runtime ignores the scene" — it is that **no `GameObject`,
`Transform`, or managed object is touched inside a job.** The main thread flattens live state into
`NativeArray`s before jobs start. That gather is a loop over a few hundred components: microseconds, and it buys back everything that makes a Unity-based renderer worth using.

### Live gather (each frame, main thread)

- **Camera** — a `CpuCamera` GameObject whose `Transform` **is** the render origin, visible and
  manipulable in the Scene view, framing exactly what gets rendered.
- **Props** — every enabled `MeshRenderer` that is not Static, found afresh each frame, so props
  spawned, destroyed or hidden at runtime need no bookkeeping. A main-thread loop copies each one's
  `localToWorldMatrix` into a `NativeArray`, plus its current ambient volume (§4). Doors, platforms,
  pickups, monsters, projectiles, gibs.
- **Mesh library** — the first time a prop's mesh is seen, it is read into local-space vertex and
  index buffers plus face normals, keyed by `Mesh`. Every prop using that mesh shares the entry. The
  library only grows; the set of distinct meshes is small, so nothing is ever removed.
- **Shadow blobs** — position and shape of each `BlobShadow` component (§4).
- **Light parameters** — position, color, radius, and which visibility channel the light owns. §4
  keeps colour and intensity live while the bake freezes only geometry; this gather is what makes
  that true. Flickering torches and muzzle flashes are the Quake identity.
- **Sun** — direction, colour and intensity of the single optional directional light (§4). Not part
  of the per-tile light list.
- **Entity shadow rays** — for each sampled object, one batched ray per light in range (§4),
  scheduled with the frame's other jobs.
- **Animation time / clip selection** per animated prop.

### Bake output (immutable blob asset)

- **No geometry.** Every mesh, Static or not, is read live into the mesh library (Live gather), which
  is what lets prefabs be instantiated at runtime. **Static = Unity's Static flag**: it marks what
  Unity's lightmapper bakes, and promises the object never moves, which baked visibility depends on
  (§4). Pre-transforming static geometry to world space is deferred (§9).
- Per-triangle surface descriptor index
- POT textures with mip chains, indexed by ID (**no atlasing** — see gotcha below)
- Baked vertex-keyframe animation clips
- Per-surface descriptors: `{ surfaceType, textureId, twoSided, ignoresBlobs }`
- Per-triangle ambient volume index for static geometry, resolved at bake and never recomputed (§4)
- **Per-(object, light) visibility slices** in one flat byte array, transcoded out of Unity's
  shadowmask with the owning light per channel resolved at bake, so the runtime holds no atlas index,
  channel packing or lightmap dependency of its own. Addressed by
  `{ visTexelOffset, visWidth, visHeight, visLightFirst, visLightCount }` on the **per-object**
  record beside the vertex ranges — it is geometric data, not material data, so it does not sit on
  the surface descriptor (§4)
- Ambient volume definitions — AABB plus sky and ground colour, smallest-containing wins (§4)

**Winding and mirroring.** Face normals are always the cross product in the mesh's own local
winding, taken to world space by the inverse-transpose of the object's 3×3 matrix each frame (§3). That keeps them pointing outward under mirroring (`scale.x = -1`),
and the screen winding that mirroring flips is absorbed by setup (§3), so there is no index swap and
no per-instance flag. **Never compute a world normal from the cross product of world-space vertices**
— mirrored objects would light inside out and cull wrong. Meshes with inconsistent winding are not
detected; a mis-wound triangle shows up as a hole.

Bake automatically on entering Play mode, plus an explicit button. Every Play session sees the
current scene, with no dirty-tracking.

### Texture constraints (pipeline, not aesthetic)

- **Source pixels come from the source file, not the imported texture.** Import settings stay at
  their defaults (compressed, non-readable — fine for the Scene view). The bake decodes the texture's
  source file from disk (PNG/JPG natively, TGA via a small decoder, PSD unsupported) into a temporary
  texture, copies the pixels out, and destroys it immediately — one texture in memory at a time.
  Reading the BC-compressed import instead would bake block artifacts into texels that end up chunky
  and magnified on screen.
- **Unity textures are never sampled.** Meshes are read from their `MeshFilter`s at runtime (Live
  gather), so every mesh needs Read/Write enabled. The display camera's culling mask is Nothing —
  URP draws only the `RawImage` (Output path).
- **Power-of-two, enforced.** The real reason is the mip chain: each level is exactly half, one stride
  formula, one shift per level, and the whole chain fits a single allocation at 4/3 the base size with
  a trivial offset table. Wrap becoming an `AND` is a minor bonus, not the justification. The bake
  step resamples, so this costs the artist nothing.
- **Build the chain in linear space.** Decode to linear, 2×2 box average, re-encode to gamma for
  storage. Averaging gamma values directly darkens high-contrast detail at every level, so distant
  textures dull and each level switch becomes a brightness jump. Bake-time, zero runtime cost.
- **Sharpening is a later lever, not a default.** A box filter also loses contrast per level; a
  Kaiser or mild unsharp resample would make level `N+1` look like a resampled `N`, shrinking the
  delta the mip dither (§3) has to hide. Add it only if transitions look bad on screen.
- **Maximum resolution is a project-level setting** — dropdown of 64 / 128 / 256 / 512 / 1024, default 256. At roughly 1:1 texel:pixel this is a bigger lever on the L2 working set than POT-ness is.

### Colorspace

**Work in gamma space.** The colormap ramp is a gamma-space LUT and that is the Quake-correct
behaviour. The project is Linear, so URP's backbuffer is sRGB and encodes on every write. Create the output
`Texture2D` as sRGB (`linear: false`) so the UI shader's texture decode cancels the backbuffer's
encode, and keep post-processing off on the display camera — otherwise everything reads
washed out or too dark. **Turn HDR off on the display camera** too: the URP asset has HDR on at 32-bit
precision, so the camera renders into R11G11B10, whose 6-bit (blue: 5-bit) mantissa cannot hold
adjacent 8-bit sRGB codes apart near white — bright colours come back a code or two off. Pin this at
milestone 1; it is miserable to discover at milestone 6.

### Output path

Write jobs directly into `texture.GetRawTextureData<Color32>()`, then `Apply(false, false)`. The
texture therefore **is** the color buffer, so it is created at the padded size (656×384 for 640×360,
§3) and the display crops the padding via UVs — a 640×360 texture would not match the row pitch.

**Row 0 is the bottom row**, matching Unity's texture layout — and the natural result of mapping
clip-space `y` to screen, so nothing is flipped anywhere. The padding sits at the top and right.

**Display: a `RawImage` on a Screen Space – Overlay canvas.** URP draws overlay UI itself, after the
camera and at native resolution, so there is no render-pipeline code: no `ScriptableRendererFeature`
and no RenderGraph pass (the project runs URP with RenderGraph, compatibility mode off). Setup:

- `filterMode = Point` on the texture; `uvRect = (0, 0, 640/656, 360/384)` crops the padding.
- Canvas Scaler set to Constant Pixel Size, scale factor 1.
- **Display scaling:** the largest integer scale at which the internal resolution fits the screen,
  centred, with black bars on whatever is left over (e.g. 640×360 on 3440×1440 ultrawide → ×4 =
  2560×1440, pillarboxed). Never a non-integer stretch. Recompute the scale when the window size
  changes; the internal resolution itself stays fixed.
- **Round the centring offset down to a whole pixel.** An odd leftover would put the image half a
  pixel off, and point sampling would then make some texels one display pixel wider than others.
- The display camera clears to **Solid Color black** — the bars are that clear. Culling mask Nothing,
  HDR and post-processing off (Colorspace).

If post effects ever need to run over the image, that is the point to move to a
`ScriptableRendererFeature`; nothing planned needs it.

- Zero-copy into the texture's **CPU-side** storage. `Apply()` is still a real, synchronous CPU→GPU
  upload on the main thread — sub-millisecond at ~1 MB, but not free.
- Re-acquire the `NativeArray` each frame rather than caching it; the handle is invalidated by
  `Apply()`.
- Later options if the upload shows up in a profile: double-buffered `Texture2D`, or a `GraphicsBuffer`
  sampled by a custom display shader.

### Threading

Complete all jobs same-frame. One-frame-behind pipelining is deferred — it trades determinism and debuggability for throughput, and neither should be spent before a measurement says it's needed.

### Memory

Persistent `NativeArray`s only, no per-frame allocator. Each per-frame buffer has a known bound or an
exact size after its count pass; on the rare frame one is too small, reallocate it larger and keep
it. **Measure in a build**
— Editor safety checks and the Jobs Debugger make Burst code look 5–10× slower than it is.

### Packages to add

`com.unity.burst`, `com.unity.mathematics`, `com.unity.collections` — already resolved transitively
through URP, but add them to `Packages/manifest.json` as direct dependencies so their versions stay fixed.

### Asset-store level import gotcha

**Texture atlases / trim sheets bleed across cells when the mip chain is built.** Nearest sampling
doesn't help; the averaging happens at mip construction. Prefer a kit with individual per-surface
textures. Otherwise: cap mip levels, pad cells at bake, or split the atlas during extraction.

Extraction also discards tangents and UV2, enforces POT, and applies the resolution cap.

---

## 8. Culling

**Frustum culling (objects) + backface culling (triangles).** Both are in the §3 pipeline.

Frustum culling tests each renderer's world-space AABB (`Renderer.bounds`, which Unity keeps current). If the
profile shows culling costing close to what it saves, replace it with something cheaper to traverse
(e.g. a coarse spatial grid or BVH over the same AABBs).

No occlusion culling. It only attacks *geometry* throughput, and there is no evidence that is a problem at demo scale.

**Trigger to revisit:** when per-triangle work dominates the profile. At that point, **software occlusion culling** (rasterize a 256×128 depth buffer of the occluders, test AABBs) is the right answer — it reuses the rasterizer, and occluders are marked with Unity's existing static flags (Occluder Static), so there is no bespoke authoring tool.

---

## 9. Rejected / deferred (with reasons)

| Idea | Verdict | Why |
|---|---|---|
| **Surface cache** (Quake-style texture×lightmap caching) | **Rejected** | Solves "shading too expensive to redo per frame." At our resolutions that problem doesn't exist. Cache entries are ~1:1 texel:pixel, so re-cooking costs about the same as shading directly — it's purely a *static* lighting amortization. Right answer for a Pentium 90, or for 4K / thousands of static lights. |
| **Irradiance lightmaps** (Unity's colour lightmap) | **Rejected** | Bakes the *product*, welding light intensity, colour and falloff into the texels — it would kill the live gather that makes flickering torches work (§7), and Unity's inverse-square falloff is not the linear-with-cutoff one §4 uses. Consequence: no indirect light; ambient volumes are the substitute (§4), which is Quake-appropriate. |
| **Visibility-only bake** (Unity Shadowmask, transcoded) | **Adopted → §4** | Bakes the *factor* instead, so every other term stays live. The original objections do not apply to it: Unity supplies the unwrap and the atlas, and we read the result rather than writing a lightmapper. Measured viable — `Experiments/shadowmask-findings.md`. Price is a second interpolated UV set and hard-edged shadows. |
| **Our own ray bake** (same `uv2` substrate) | **Deferred** | The replacement producer behind §4's visibility format, which is why that format and not Shadowmask is the design decision. Roughly 250 marginal lines and two weekends once interpolation exists, and it deletes the transcode. **Triggers:** wanting real penumbra, which Shadowmask cannot store at any resolution; or the four-shadowing-light budget biting after range tightening and non-casting fill lights have both been tried. |
| **Vertex lighting for props** | **Rejected** | Looks terrible on 2-triangle walls — precisely why Quake used lightmaps — and would be a second lighting path to maintain. |
| **Depth prepass** | **Deferred** | Bounds shading to once per pixel but pays a second per-pixel raster pass, and alpha-tested triangles need a texture read in it. Second step of the §3 overdraw escalation, after the front-to-back sort. Also the prerequisite for depth-bounded light culling. |
| **Visibility buffer** (tri ID in prepass) | **Rejected** | Extra complexity with no demo benefit; it turns attribute interpolation — now two UV sets (§4) — into per-pixel gathers. Revisit only if a profile demands it. |
| **Portals / BSP for culling** | **Rejected** | Not needed for overdraw *or* geometry at demo scale. Light leaking, its only remaining justification, is solved by baked visibility (§4). |
| **Stencil shadow volumes** | **Rejected** | The per-tile stencil byte really is trivial — that's the easy 10%. The real work is per-light silhouette edge extraction every frame, which is a lot of per-triangle work and a lot of code. Baked visibility plus contact blobs are the right call for a demo. |
| **Decals** | **Rejected** | Not needed to show a lit stylized scene; coplanar geometry would also need a depth bias the rasterizer doesn't have. |
| **Additive blending** | **Deferred** | Our main target doesn't use it: software Quake writes opaque palette colours with depth, GLQuake's binary-alpha dot makes its blend a no-op, Quake 2 over-blends, Build uses a translucency table. Half-Life 1 and Unreal 1 *do* have it, but only for light-source polish — glows, coronas, teleporter shimmer, explosion flare. It cost the opaque/additive split in the §3 per-tile loop, a structure Pixel Sorted Transparency (§5) would then delete. If it returns it arrives *inside* Pixel Sorted Transparency, where an additive fragment is just another entry in the per-pixel slots — never as a stage of its own. |
| **Realtime raytraced shadows** (BVH, SIMD packet traversal) | **Optional, late** | Per-pixel rays against real geometry, as against the offline bake above. Needs a BVH and per-ray traversal; the cost is unknown until measured, so do not plan around it. If tried: one ray per 2×2 block, one or two shadowed lights, layered on top of the baked term rather than replacing it. |
| **Analytic blocker volumes** | **Possible, not planned** | Invisible convex volumes, gathered per tile as (light, blocker) pairs by shadow-region screen rect and tested per pixel against the segment pixel→light: ~18 flops, no memory traffic, free penumbra, and cheapest of all for the sun (unit direction, no divide). It is §4's contact blob with the light direction put back in — same gather, same opt-in component, same already-reconstructed world position — so it multiplies one light's term and leaves ambient alone. **Intended use is large moving geometry** (platforms, doors) that the bake cannot follow, and it would subsume the directional half of contact blobs. Useless for the level shell: that needs one authored volume per occluder, and there the thing blocking light is the level itself. |
| **Occlusion probes** (baked grid, trilinear lookup) | **Possible, not planned** | Would replace §4's per-object shadow rays for sampled movers: no collider dependency, correct for flying objects, interpolated rather than binary, and traced against the same *meshes* the bake uses — which is the one thing those rays get wrong. A grid carrying ambient per position could also subsume ambient volumes, but that part is not a pure win: it trades §4's authored art direction for a GI approximation, and no bake can infer a deliberately blue-tinted cave. `UnityEngine.LightTransport.RadeonRaysProbeIntegrator.IntegrateOcclusion` computes occlusion at arbitrary positions against scene meshes, so Unity would do the tracing; untested. |
| **Imported vertex normals + interpolation** | **Deferred** | Would let artists author smooth props. Adds a normal vertex attribute, its interpolation and a per-pixel normalize, where a face normal needs no per-pixel work. It also splits shading normal from culling normal into two channels that can disagree, so a mesh with good winding and bad normals renders visible and wrong. Additive change if smooth props are ever wanted; not a rewrite. |
| **Static geometry pre-transformed to world space** | **Deferred** | Saves nothing per vertex, since multiplying by `mvp` or by `viewProjection` costs the same, and only a matrix product and an inverse per object. The one saving that scales with geometry is shading: lighting needs world-space normals, which a local-space mesh pays for with a 3×3 multiply and a normalize per visible triangle each frame. Revisit if the milestone 6 profile shows that cost, or for other static-only optimizations (merged level chunks, spatial structures). |
| **MSAA / any antialiasing** | **Rejected** | Fights the pixel-art look and multiplies raster cost. Aliasing is the aesthetic. |
| **Bilinear filtering** | **Rejected for textures** | Same reason. It is *magnification* that makes texels chunky, and bilinear is precisely what softens them. **The visibility map is the deliberate exception** (§4): Quake also paired nearest-sampled textures with a smoothly interpolated lightmap, and without it a coarse map reads as blocky shadows rather than soft ones. |
| **Trilinear (blending between levels)** | **Rejected** | Redundant: dithered level selection (§3) already delivers the same expected result while keeping every pixel one raw texel. Trilinear would be a second sampling path with two texel fetches per pixel. |
| **One-frame-behind pipelining** | **Deferred** | Real throughput win, but costs determinism and debuggability. Not before a measurement demands it. |
| **GPU / compute-shader hybrid** | **Rejected** | Defeats the premise of the project. |
| **8-bit indexed color pipeline** | **Rejected** | 32-bit chosen. Keeps real alpha blending available. |
| **PS1 vertex jitter** | **Rejected** | Wrong heritage. Quake was subpixel-accurate. |
| **CPU skinning** | **Rejected** | Baked vertex keyframes are simpler, faster, and produce the desired floatiness. Forecloses procedural animation; irrelevant for a flythrough. |

---

## 10. Other CPU-unique capabilities

- **Free reads of the tile's own buffers** — color and depth are in cache while shading; no resolve,
  copy, barrier, or second render target. Reading the pixel you just wrote is genuinely free.
  **Refraction and heat haze are not** — they read displaced neighbours that can cross a tile boundary,
  so they belong in a post pass.
- **Branch granularity, not free branching.** Burst vectorizes the inner loop to `float4`/`int4` (8-wide only via
  `v256` intrinsics), and a per-pixel branch inside that either falls back to scalar (4–8× loss) or becomes a select that
  evaluates both sides — exactly GPU divergence at narrower width. The real advantage is that a group is 4–8 lanes instead of 32, so early-out for a whole group is far more likely to fire.
- **One shaded pixel per covered pixel.** GPUs shade in 2×2 quads so `ddx/ddy` can be finite
  differences, making a one-pixel triangle launch four fragments. Our gradients and mip λ are analytic
  from setup (§3), so coverage and shading cost are the same number; the trade is the arbitrary
  shader-computed derivatives quads exist for, which hand-written loops don't need. **Worth little with
  giant triangles** — a Quake kit at 640×360 is thousands of pixels per triangle — but it is why Nanite
  rasterizes small triangles in a compute shader rather than through the hardware path.
- **No draw-call or state-change cost.** Thousands of distinct materials with zero batching
  concerns, no material sorting, no pipeline state objects.
- **Irregular per-tile data structures** — the per-pixel OIT fragment lists of §5 are a plain array and
  a plain counter, which is the thing GPUs cannot do cheaply.
- **Deterministic frames** — bit-exact reproducible for the same build on the same machine (Burst's
  FMA use and approximate `rcp`/`rsqrt` differ across CPUs); serialize input state, including frame times, and step through the renderer. Determinism comes from tile-private pixel ownership plus deterministic binning order (§3); that is why atomic append is rejected. Debugging superpower.

---

## 11. Debug / visualization layer

Treat as a **feature, not dev tooling** — it's a portfolio artifact in its own right. A tile-cost
heatmap and a core-scaling graph communicate "I understand parallel architecture" faster than any
screenshot of the render.

**Minimal instrumentation arrives with binning (milestone 3)**, because the tile-cost heatmap and the
triangles-per-tile histogram are *how you find out whether the binner works*. The polished set follows
later: overdraw heatmap · tile occupancy/cost heatmap · render-only-tile-N isolation · per-stage job timings · core-scaling graph · triangle-density sweep.

The density sweep is a procedurally generated set of quads at several subdivision levels: identical
pixel coverage, rising triangle count, which separates per-triangle cost from per-pixel cost and is
where §10's one-pixel-per-pixel property would show against a quad-shading GPU. A generated scene,
not a renderer feature.

---

## 12. Milestones

| #   | Milestone |
| --- | --- |
| 1   | Framebuffer + integer-scaled `RawImage` display with black bars + clear — prove the output path first. **Pin colorspace here.** |
| 2   | Single-threaded triangle raster, flat color, z-buffer, fixed-point edges, near clip + guard-band fallback, backface culling |
| 3   | **Tile binning + parallel jobs — the architectural spine.** Includes *minimal* scene extraction (every enabled `MeshRenderer` drawn through the mesh library, per-object AABBs + frustum culling; no Static baking, textures, materials or animation), minimal instrumentation, a hand-built expensive benchmark scene with a lockable camera, and the first profile (§3 cost model). Binning proves nothing on hardcoded triangles, and nothing is measurable without a repeatable scene. |
| 4   | Texture bake (POT resample, mip chain) + perspective texture mapping, nearest sampling, dithered mip selection (§3) |
| 5   | Rest of the bake (materials, surface descriptors, per-triangle ambient volume indices, visibility transcode, blob asset) + live gather (prop transforms, blob shadows, lights, sun, animation time) |
| 6   | Per-tile light gather + colormap ramp + ambient volumes + baked visibility sampling (adds the second interpolated UV set to §3's setup) + optional sun + `LitTextured` / `Fullbright` / `AlphaTested` |
| 7   | Props, baked vertex animation, contact blobs, per-object shadow rays for sampled movers (§4) |
| 8   | `Sky` / `Liquid` / `Particle` if wanted |
| 9   | Debug views, full set |
| 10  | **Performance pass** — act only where the profile points: overdraw escalation (§3), tile size, framebuffer layout, span-interpolated mip λ. |
| —   | *Optional:* raytraced shadows, Pixel Sorted Transparency (§5), specular |

Get to milestone 3 fast; it proves the thesis. Don't polish 1–2.

---

## 13. Open questions

- **How visible is the mip dither at ×3 / ×5 upscale?** The scheme is settled (§3); the amplitude is
  not. It is the mip-to-mip delta, so it is largest on exactly the high-contrast textures a Quake kit
  is made of, and at ×3 each renderer pixel is a 3×3 display block that will not visually average.
  The bet is that a period-correct ordered pattern is *read* as intentional at that amplitude. Levers,
  in descending order of confidence: adding sharpening to mip generation (§7), mip bias, then swapping
  the dither table. Judge it on the milestone 3 benchmark scene once it is textured (milestone 4), with a
  runtime toggle, not from a still.
- Tile size (16 / 32 / 64) and framebuffer layout (linear shared vs. tile-major private) — benchmark
  at milestone 10, don't guess.
- Number of quantization steps in the light ramp, and whether to dither before quantizing.
- Whether the level kit ships individual textures or a trim sheet (affects the §7 gotcha).
- **How do props' materials reach the texture bake?** Props keep their `MeshRenderer`, and a prefab
  that only appears at runtime is in no scene, so a bake that walks the scenes would miss its
  materials. Settle it with the texture bake (milestone 4).
- **Lightmap texel density**, which is an *inverse* softness dial: coarser is both cheaper and
  softer, because the only blur available is a texel's own footprint (§4). So the usual instinct is
  backwards here, and the answer is a judgement call on the milestone 6 scene, not a number to pick
  up front.
- **Does a Mixed directional light consume a shadowmask channel everywhere?** §4 assumes it does,
  since its volume is unbounded, which would leave three for point lights across the whole level.
  Inferred, never measured — one bake of a directional plus five overlapping point lights settles it.
- **Is `Light.shadowStrength` baked into the texels or applied after sampling?** It was pinned at 1
  for every measurement, so this is untested. If it bakes in, a transcode that also applies it
  double-darkens every shadow.
- **Chart quality of `Unwrapping.GenerateSecondaryUVSet` on hand-modelled non-planar geometry.**
  The whole visibility route rests on `uv2`, and it has only been exercised on primitives.
