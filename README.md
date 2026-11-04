# Unity CPU Renderer

A tile-binned software rasterizer for Unity 6, written with the Jobs system and Burst. Unity is used
as the level editor and the host application. Every pixel on screen is computed on the CPU and
uploaded as a texture.

The look is stylized and deliberately feature-light, inspired by Quake 1's software renderer:
low internal resolution with integer upscaling, nearest-neighbour texture sampling with mips, a
colormap-style light ramp, and faceted per-triangle lighting.

## Status

**Under active development.** This is a learning project and a portfolio tech demo, not a
production-ready renderer. Expect missing features and breaking changes.

- [x] Milestone 1: framebuffer and integer-scaled output to screen
- [x] Milestone 2: single-threaded triangle raster, z-buffer, fixed-point edges, near clipping, backface culling
- [ ] Milestone 3: tile binning and parallel jobs (in progress)
- [ ] Textures, baked lighting, props, animation, debug views, performance pass

The full design and milestone list live in [CpuRendererDocs/DESIGN.md](CpuRendererDocs/DESIGN.md).

## Requirements

- Unity 6 (6000.3)
- Universal Render Pipeline, Burst, Collections and Mathematics packages (resolved through
  `Packages/manifest.json`)

Open the project in Unity and play `Assets/Scenes/SampleScene.unity`.

## Use of AI

I wrote all of the code by hand.

AI (Claude) is used for:
- drafting and editing the Markdown documentation, including the design doc and this README
- research, explaining concepts and reviewing code I've written
