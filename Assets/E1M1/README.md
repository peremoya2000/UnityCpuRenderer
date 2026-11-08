# E1M1 demo level

The geometry of Quake's first level, *the Slipgate Complex*, converted into an ordinary Unity scene. It is the
CPU renderer's benchmark and demo level.

No data from the Quake game is used. Only two freely licensed sources are used:

- **The map source.** In 2006 John Romero released the original Quake `.map` files under the GNU GPL, version 2.
- **The textures.** These are free replacements that keep the original texture names.

This project is not affiliated with or endorsed by id Software. Quake is a trademark of ZeniMax Media.

## Licences

| What | Licence |
|---|---|
| Everything in this folder except `Textures/` | GPL-2.0, see `Source~/gnu.txt` |
| `Textures/` | CC0 / MIT-0 / 0BSD, see `Source~/QuadCompati-README.md` |

The GPL covers the map and everything derived from it: the model (`e1m1.obj`, `e1m1.mtl`), the scene, the
materials, and the tools that produce them. The `.map` file is included as the source form of the level.

The renderer elsewhere in this repository only reads this level as data. It is a separate work and is not
covered by this folder's licence.

## Sources

| File | Origin |
|---|---|
| `Source~/E1M1.MAP`, `gnu.txt`, `release_readme.txt` | Unmodified, from <https://rome.ro/s/1996-quake-map-sources.zip>, linked from <https://rome.ro/resources>. Zip SHA-256 `c2f0660617264e0918edc4a7c34dc66692ef396f2308c6cf9b6d12cfe595db7c`; `E1M1.MAP` SHA-256 `aab3e8d425ba951702395189ada3ba0dc63cf9f1f08d395d86e0cddbcb321a83` |
| `Textures/*.png` | Unmodified, from [LibreQuake/QuadCompati](https://github.com/LibreQuake/QuadCompati) v0.01, `texture-wads/qcompat_base`. Zip SHA-256 `4ce8732fe96f7be1469c5cb07d73287c293f7fc1f6dad96db88b099975b6e42f`. Only the 68 textures this level uses are included |
| `Source~/QuadCompati-README.md` | QuadCompati's README at commit `bbd63312`, which states the textures' licence |

Unity ignores folders whose name ends in `~`, so `Source~` is in the repository without being imported.

## How the level was built

`Source~/map_to_obj.py` (Python 3, no dependencies) converts the map the way Quake's compiler, qbsp, did:

1. It clips each brush's planes into face polygons.
2. It runs CSG between the brushes of each entity. Faces inside other brushes are removed, and where two faces
   on the same plane overlap, the later brush wins.
3. It removes faces that face the void outside the level.
4. It merges coplanar fragments and fixes T-junctions.
5. It projects textures with Quake's axis-aligned projection.

Its checks fail the conversion if:
- any coplanar overlap (z-fighting) or T-junction remains,
- any visible open edge remains,
- any removed face can be seen from inside the level,
- any triangle is wound wrongly.

Coordinates are Unity = (x, z, y) / 32, so 1 Unity unit is 32 Quake units.

Each brush entity (doors, buttons, platforms, secret walls) becomes its own object. Trigger volumes and clip
brushes are dropped. The rest of the level is split into 16 m chunks.

The scene has no door logic. Instead, doors start open and platforms start lowered, at the positions Quake's own
game code computes, so the whole level is reachable:
- **Doors** move along their angle by their size minus an 8-unit lip.
- **Platforms** start at the bottom of their travel.
- **The bridge** that a button extends starts retracted, as it does in Quake.
- **Secret doors and buttons** stay where the map draws them.

To regenerate the model from the files in this folder:

```
cd Assets/E1M1/Source~
python map_to_obj.py E1M1.MAP ../Textures .. e1m1_entities.json
```

`Tools > Build E1M1 Scene` (in `Editor/E1M1SceneBuilder.cs`) then:
- sets the model's import settings,
- creates one URP Lit material per texture,
- builds `E1M1.unity` from the model, the 198 lights and 5 viewpoints in `e1m1_entities.json`.

The lights are converted from the map's `light` entities. Quake's light value `L` gives a linear falloff that
reaches zero at distance `L`, so each light has range `L / 32` and intensity `L / 300`. The viewpoints are the
player start and the map's four intermission cameras.

Both tools were written by Claude (Anthropic).
