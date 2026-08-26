# The UI kit

The theme is built on the Fantasy Nordic GUI kit, which is a Photoshop file for
game UI, not a web framework. Turning it into a website meant slicing assets out
of it. This is how that works and how to get more.

## What the kit actually is

One 690MB PSD with 1048 layers, plus the Vollkorn typeface. The kit lives in
`~/Downloads/fantasynordicgui.zip` and is not in this repo (it is licensed art).

Two facts shape everything downstream:

**The structural art is greyscale, but the kit is not.** Every frame, bar,
plaque and divider is neutral stone: 25 to 37 percent grey with near-white
silver trim, no hue at all. That is why the theme looked monochrome at first.

The colour lives in a handful of layers. Measuring mean saturation across all
1048 of them (`-colorspace HSL -channel G -separate`) finds 61 layers above 15
percent, topping out at 61 percent. What is in there:

| Source | Value |
|---|---|
| Active nav labels | `#f18d3b` amber |
| Glow layer | `#28706f` teal |
| Paper texture | `#c4a177` parchment |
| Skull crest | `#88684a` bronze |

There is one more finding worth recording: every full-canvas backdrop layer in
the kit measures hue 231, just desaturated to near-grey (`#131315`, `#212225`).
So blue is the kit's own cool bias, which is why the logo's blues sit on this
art without a fight.

The palette in `rmv.css` now comes from the guild logo rather than from the kit,
since the logo is the brand. See `docs/logo.md`. The kit values above are what
to fall back to if the logo ever changes hue.

To find the coloured layers yourself:

```bash
magick -quiet kit.psd -colorspace HSL -channel G -separate \
  -format '%[scene] %[fx:mean*100]\n' info: | sort -k2 -rn | head -40
```

**Layer names are useless.** 275 layers are called `bg`, 274 are called `frame`.
There is nothing to key an automated export off, so the layer index for each
asset is recorded by hand in `tools/slice-nordic-ui.sh`. Those indices are tied
to this exact PSD: if the kit is ever updated they move, and the manifest has to
be rebuilt.

## Getting the assets

```bash
./tools/slice-nordic-ui.sh              # slice everything in the manifest + fonts
./tools/slice-nordic-ui.sh --enumerate  # dump all 1048 layers with sizes
./tools/slice-nordic-ui.sh --contact-sheet 401,470,496
```

The contact sheet is how new assets get picked: render a batch of candidate
layers as a labelled grid, look at it, add the good ones to the manifest with a
sensible max width. Output lands in `src/Rmv.Web/wwwroot/img/ui/`, currently 22
files at 860KB total.

The kit art is authored for 4K game UI, so most layers are two to three times
larger than a web page needs. The third manifest column is the widest the asset
is ever rendered at, doubled for retina. Without it the asset set is 2.7MB.

## Border slices

```bash
./tools/measure-border-slices.sh > src/Rmv.Web/wwwroot/css/_slices.css
```

`border-image` needs to know how far a corner ornament reaches, or it stretches
the corner art along the edge and smears it. The script collapses each frame's
alpha channel to a one-pixel strip, which gives mean density per column, and
finds where the ornament stops exceeding the plain-border baseline.

It gets most frames right and misreads two, so those carry explicit overrides
with the reason recorded in the script. Do not hand-edit `_slices.css`; change
the override and regenerate.

## Which technique suits which asset

This is the rule that took the longest to work out, and getting it wrong is what
made the first few builds look broken.

1. **Box frames** (`panel-frame-card`, `panel-frame-inset`) use `border-image`.
   That is what it is for: it pins the four corners and stretches only the
   straight edges.

2. **Fills behind a frame** must be CSS gradients, not stretched art. The bar
   and button textures have decorated ends baked in. Squashing a 16:1 asset into
   a 4:1 box leaves those ends misaligned with the frame, which reads as a
   doubled outline. `border-image` has stretch logic; `background-image` has
   none.

3. **Anything with a background image under a `border-image`** needs
   `background-origin: border-box` and `background-clip: border-box`.
   `border-image` paints on the border box, so a background clipped to the
   content box shows as a visible inset rectangle.

4. **Patterns tile horizontally, never vertically.** Both `pattern-lattice` and
   `pattern-knotwork` fade from dense at the top to nothing at the bottom, which
   is exactly what a hero wants. Size them to the container height and
   `repeat-x`: the fade shows in full and no edge is ever visible.

## Asset inventory

Eleven of the 22 sliced assets are not referenced by any stylesheet:
`input-frame`, `progress-track`, `progress-fill`, `header-bar-bg`, `navbar-bg`,
`button-bg`, `button-bg-pill`, `divider-wide`, `panel-frame-tall`,
`panel-frame-inset`, `slot-frame-notched`.

The first three are wanted by the file-upload tool, so they are kept rather than
deleted and re-sliced. `header-bar-bg`, `navbar-bg`, `button-bg` and
`button-bg-pill` lost their jobs to rule 2 above. `panel-frame-tall` and
`panel-frame-inset` are the two frames no slice can place; see the note in
`tools/measure-border-slices.sh`.

## The kit's own crest

Layer 420 is the kit's one piece of full-colour art, a bronze horned skull. It
stood in as the emblem before the guild logo existed and is no longer sliced.
The layer index is recorded here in case it is ever wanted again.

## Fonts

Vollkorn ships in the kit under the SIL Open Font License, copied to
`wwwroot/fonts/` alongside `OFL.txt`. Four weights are converted to woff2 and
the other ten are dropped, so 4.5MB of TTF becomes 460KB.
