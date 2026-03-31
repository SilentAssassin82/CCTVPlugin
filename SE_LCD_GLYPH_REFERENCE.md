# Space Engineers LCD Monospace Font — Confirmed Glyph Reference

Glyphs confirmed present in SE's **Monospace** font (`FontDataPA.xml`, `aw=24`, glyph size `30×42`)
and verified as **white alpha-mask DDS** (tintable with font colour) via the in-game tint test.

## How to Test a Glyph

1. Place two LCD panels side-by-side in **Monospace** font.
2. Set panel A font colour to **cyan**, panel B to **white**.
3. Type the target character on both panels.
4. **Pass**: character appears in cyan on A and white on B (white alpha-mask — tints correctly).
5. **Fail**: character appears identical on both panels regardless of tint, or is invisible / wrong colour.

---

## Confirmed Working — Grayscale Density Ramp

These are ordered by visual fill density on the LCD panel.
Density values marked `*` are IEEE-standardised pixel fill; all others are in-game visual estimates.

| Char | Codepoint | Density | Notes |
|------|-----------|---------|-------|
| ` ` (space) | U+0020 | 0 % | Standard ASCII |
| `.` | U+002E | ~4 % | Standard ASCII |
| `*` | U+002A | ~10 % | Standard ASCII |
| `!` | U+0021 | ~15 % | Standard ASCII |
| (PUA grid) | U+E033 | ~19 % | Private-use glyph; confirmed tintable. Small grid/dot pattern. Fills the 15 %→25 % gap. |
| `░` | U+2591 | 25 % * | LIGHT SHADE — standard block element |
| `▒` | U+2592 | 50 % * | MEDIUM SHADE — standard block element |
| `☻` | U+263B | ~65 % | BLACK SMILING FACE. Confirmed tintable. Fills the 50 %→75 % gap. Adjust density if banding visible. |
| `▓` | U+2593 | 75 % * | DARK SHADE — standard block element |
| `■` | U+25A0 | ~82 % | BLACK SQUARE. Solid square, slightly smaller than full block. |
| `█` | U+2588 | 100 % * | FULL BLOCK — standard block element |

---

## Confirmed Working — Box Drawing / Structural

### Light Corners (U+250C–U+2518)

Useful for borders, UI overlays, or adding fine structural detail.
All four light corners confirmed tintable.

| Char | Codepoint | Shape | Density est. | Notes |
|------|-----------|-------|-------------|-------|
| `┌` | U+250C | Top-left corner | ~12 % | BOX DRAWINGS LIGHT DOWN AND RIGHT |
| `┐` | U+2510 | Top-right corner | ~12 % | BOX DRAWINGS LIGHT UP AND LEFT |
| `└` | U+2514 | Bottom-left corner | ~12 % | BOX DRAWINGS LIGHT UP AND RIGHT |
| `┘` | U+2518 | Bottom-right corner | ~12 % | BOX DRAWINGS LIGHT DOWN AND LEFT |

### Double-Line and Mixed Box Drawing (U+2551–U+256C)

Full set confirmed tintable. Useful for heavier borders, double-walled panels, and mixed single/double line UI frames.

| Char | Codepoint | Description |
|------|-----------|-------------|
| `║` | U+2551 | DOUBLE VERTICAL |
| `╒` | U+2552 | DOWN SINGLE AND RIGHT DOUBLE |
| `╓` | U+2553 | DOWN DOUBLE AND RIGHT SINGLE |
| `╔` | U+2554 | DOUBLE DOWN AND RIGHT |
| `╕` | U+2555 | DOWN SINGLE AND LEFT DOUBLE |
| `╖` | U+2556 | DOWN DOUBLE AND LEFT SINGLE |
| `╗` | U+2557 | DOUBLE DOWN AND LEFT |
| `╘` | U+2558 | UP SINGLE AND RIGHT DOUBLE |
| `╙` | U+2559 | UP DOUBLE AND RIGHT SINGLE |
| `╚` | U+255A | DOUBLE UP AND RIGHT |
| `╛` | U+255B | UP SINGLE AND LEFT DOUBLE |
| `╜` | U+255C | UP DOUBLE AND LEFT SINGLE |
| `╝` | U+255D | DOUBLE UP AND LEFT |
| `╞` | U+255E | VERTICAL SINGLE AND RIGHT DOUBLE |
| `╟` | U+255F | VERTICAL DOUBLE AND RIGHT SINGLE |
| `╠` | U+2560 | DOUBLE VERTICAL AND RIGHT |
| `╡` | U+2561 | VERTICAL SINGLE AND LEFT DOUBLE |
| `╢` | U+2562 | VERTICAL DOUBLE AND LEFT SINGLE |
| `╣` | U+2563 | DOUBLE VERTICAL AND LEFT |
| `╤` | U+2564 | DOWN SINGLE AND HORIZONTAL DOUBLE |
| `╥` | U+2565 | DOWN DOUBLE AND HORIZONTAL SINGLE |
| `╦` | U+2566 | DOUBLE DOWN AND HORIZONTAL |
| `╧` | U+2567 | UP SINGLE AND HORIZONTAL DOUBLE |
| `╨` | U+2568 | UP DOUBLE AND HORIZONTAL SINGLE |
| `╩` | U+2569 | DOUBLE UP AND HORIZONTAL |
| `╪` | U+256A | VERTICAL SINGLE AND HORIZONTAL DOUBLE |
| `╫` | U+256B | VERTICAL DOUBLE AND HORIZONTAL SINGLE |
| `╬` | U+256C | DOUBLE VERTICAL AND HORIZONTAL |

### Light Lines, T-Junctions & Cross (U+2500–U+253C)

Completes the single-line box drawing set alongside the confirmed corners.

| Char | Codepoint | Description |
|------|-----------|-------------|
| `─` | U+2500 | LIGHT HORIZONTAL |
| `│` | U+2502 | LIGHT VERTICAL |
| `├` | U+251C | LIGHT VERTICAL AND RIGHT (T left) |
| `┤` | U+2524 | LIGHT VERTICAL AND LEFT (T right) |
| `┬` | U+252C | LIGHT DOWN AND HORIZONTAL (T top) |
| `┴` | U+2534 | LIGHT UP AND HORIZONTAL (T bottom) |
| `┼` | U+253C | LIGHT VERTICAL AND HORIZONTAL (cross) |

### Partial Block Elements (U+2580–U+2590)

Partial fills — useful for sub-pixel-accurate density ramps or directional shading effects.
Density values marked `*` are standardised pixel fill percentages.

| Char | Codepoint | Density | Description |
|------|-----------|---------|-------------|
| `▀` | U+2580 | 50 % * | UPPER HALF BLOCK |
| `▁` | U+2581 | 12.5 % * | LOWER ONE EIGHTH BLOCK |
| `▄` | U+2584 | 50 % * | LOWER HALF BLOCK |
| `▌` | U+258C | 50 % * | LEFT HALF BLOCK |
| `▐` | U+2590 | 50 % * | RIGHT HALF BLOCK |

---

## Confirmed Working — Geometric Shapes & Symbols

Confirmed tintable. Useful for icons, indicators, UI elements, and directional markers.
Density values are visual estimates only — not standardised.

| Char | Codepoint | Density est. | Description |
|------|-----------|-------------|-------------|
| `▪` | U+25AA | ~10 % | BLACK SMALL SQUARE — smaller than ■ |
| `▫` | U+25AB | ~5 % | WHITE SMALL SQUARE — outline only |
| `▬` | U+25AC | ~25 % | BLACK RECTANGLE — wide, short fill |
| `▲` | U+25B2 | ~50 % | BLACK UP-POINTING TRIANGLE |
| `►` | U+25BA | ~50 % | BLACK RIGHT-POINTING POINTER |
| `▼` | U+25BC | ~50 % | BLACK DOWN-POINTING TRIANGLE |
| `◄` | U+25C4 | ~50 % | BLACK LEFT-POINTING POINTER |
| `◊` | U+25CA | ~15 % | LOZENGE — diamond outline |
| `○` | U+25CB | ~15 % | WHITE CIRCLE — ring only |
| `●` | U+25CF | ~78 % | BLACK CIRCLE — solid fill |

---

## Confirmed Working — Miscellaneous Symbols

Suits, musical notes, celestial and gender symbols. Useful for icons and UI decoration.

| Char | Codepoint | Description |
|------|-----------|-------------|
| `☺` | U+263A | WHITE SMILING FACE — outline smiley |
| `☼` | U+263C | WHITE SUN WITH RAYS |
| `♀` | U+2640 | FEMALE SIGN |
| `♂` | U+2642 | MALE SIGN |
| `♠` | U+2660 | BLACK SPADE SUIT |
| `♣` | U+2663 | BLACK CLUB SUIT |
| `♥` | U+2665 | BLACK HEART SUIT |
| `♦` | U+2666 | BLACK DIAMOND SUIT |
| `♪` | U+266A | EIGHTH NOTE |
| `♫` | U+266B | BEAMED EIGHTH NOTES |

---

## Confirmed Working — Mathematical Operators & Misc Technical

Useful for scientific readouts, HUD labels, and decorative math notation.

| Char | Codepoint | Description |
|------|-----------|-------------|
| `⌂` | U+2302 | HOUSE |
| `⌐` | U+2310 | REVERSED NOT SIGN |
| `⌠` | U+2320 | TOP HALF INTEGRAL |
| `⌡` | U+2321 | BOTTOM HALF INTEGRAL |
| `∂` | U+2202 | PARTIAL DIFFERENTIAL |
| `∅` | U+2205 | EMPTY SET |
| `∆` | U+2206 | INCREMENT (delta) |
| `∈` | U+2208 | ELEMENT OF |
| `∏` | U+220F | N-ARY PRODUCT |
| `∑` | U+2211 | N-ARY SUMMATION |
| `∕` | U+2215 | DIVISION SLASH |
| `√` | U+221A | SQUARE ROOT |
| `∞` | U+221E | INFINITY |
| `∟` | U+221F | RIGHT ANGLE |
| `∩` | U+2229 | INTERSECTION |
| `∫` | U+222B | INTEGRAL |
| `≈` | U+2248 | ALMOST EQUAL TO |
| `≠` | U+2260 | NOT EQUAL TO |
| `≤` | U+2264 | LESS-THAN OR EQUAL TO |
| `≥` | U+2265 | GREATER-THAN OR EQUAL TO |
| `✓` | U+2713 | CHECK MARK |

---

## Confirmed Working — Arrows

Useful for directional indicators, UI navigation labels, and HUD elements.

| Char | Codepoint | Description |
|------|-----------|-------------|
| `←` | U+2190 | LEFTWARDS ARROW |
| `↑` | U+2191 | UPWARDS ARROW |
| `→` | U+2192 | RIGHTWARDS ARROW |
| `↓` | U+2193 | DOWNWARDS ARROW |
| `↔` | U+2194 | LEFT RIGHT ARROW |
| `↕` | U+2195 | UP DOWN ARROW |
| `↨` | U+21A8 | UP DOWN ARROW WITH BASE |

---

## Confirmed Working — Vulgar Fractions

Useful for compact numeric readouts and status displays.

| Char | Codepoint | Value | Description |
|------|-----------|-------|-------------|
| `⅐` | U+2150 | 1/7 | VULGAR FRACTION ONE SEVENTH |
| `⅑` | U+2151 | 1/9 | VULGAR FRACTION ONE NINTH |
| `⅓` | U+2153 | 1/3 | VULGAR FRACTION ONE THIRD |
| `⅔` | U+2154 | 2/3 | VULGAR FRACTION TWO THIRDS |
| `⅕` | U+2155 | 1/5 | VULGAR FRACTION ONE FIFTH |
| `⅖` | U+2156 | 2/5 | VULGAR FRACTION TWO FIFTHS |
| `⅗` | U+2157 | 3/5 | VULGAR FRACTION THREE FIFTHS |
| `⅘` | U+2158 | 4/5 | VULGAR FRACTION FOUR FIFTHS |
| `⅙` | U+2159 | 1/6 | VULGAR FRACTION ONE SIXTH |
| `⅚` | U+215A | 5/6 | VULGAR FRACTION FIVE SIXTHS |
| `⅛` | U+215B | 1/8 | VULGAR FRACTION ONE EIGHTH |
| `⅜` | U+215C | 3/8 | VULGAR FRACTION THREE EIGHTHS |
| `⅝` | U+215D | 5/8 | VULGAR FRACTION FIVE EIGHTHS |
| `⅞` | U+215E | 7/8 | VULGAR FRACTION SEVEN EIGHTHS |

---

## Confirmed Working — Letterlike & Currency Symbols

Useful for labels, credits, units, and specialised text overlays.

| Char | Codepoint | Description |
|------|-----------|-------------|
| `Ω` | U+03A9 | GREEK CAPITAL LETTER OMEGA |
| `℅` | U+2105 | CARE OF |
| `ℓ` | U+2113 | SCRIPT SMALL L |
| `№` | U+2116 | NUMERO SIGN |
| `™` | U+2122 | TRADE MARK SIGN |
| `℮` | U+212E | ESTIMATED SIGN |
| `₣` | U+20A3 | FRENCH FRANC SIGN |
| `₤` | U+20A4 | LIRA SIGN |
| `₧` | U+20A7 | PESETA SIGN |
| `₪` | U+20AA | NEW SHEKEL SIGN |
| `€` | U+20AC | EURO SIGN |

---

## Confirmed NOT Working

Characters tested and **failed** the tint test (not a white alpha-mask DDS, or missing from font).

| Char | Codepoint | Reason |
|------|-----------|--------|
| `⚳` | U+26B3 | Fails tint test — not a white alpha-mask glyph in SE Monospace |

---

## Candidate Ranges Still to Explore

These Unicode ranges are likely to contain additional tintable glyphs in SE's font.
Test using the cyan-vs-white method before adding to any ramp.

| Range | Description | Status |
|-------|-------------|--------|
| U+0370–U+03FF | Greek & Coptic | **Partially confirmed** (Ω); other Greek letters likely present |
| U+2100–U+214F | Letterlike Symbols | **Partially confirmed** (℅ ℓ № ™ ℮); others untested |
| U+2150–U+215F | Vulgar Fractions | **Largely confirmed** (⅐⅑⅓⅔⅕⅖⅗⅘⅙⅚⅛⅜⅝⅞); ½ ¼ ¾ from Latin-1 still to test |
| U+2190–U+21FF | Arrows | **Partially confirmed** (←↑→↓↔↕↨); many others untested |
| U+2200–U+22FF | Mathematical Operators | **Partially confirmed** (∂∅∆∈∏∑∕√∞∟∩∫≈≠≤≥); many others untested |
| U+2300–U+23FF | Miscellaneous Technical | **Partially confirmed** (⌂⌐⌠⌡); many others untested |
| U+2500–U+257F | Box Drawing | **U+2500–U+253C singles + U+2551–U+256C double-line fully confirmed**; mixed heavy singles still to test |
| U+2580–U+259F | Block Elements | **U+2580–U+2590 partially confirmed** (▀▁▄▌▐); U+2582–U+2587 eighth-block series still to test |
| U+25A0–U+25FF | Geometric Shapes | **Partially confirmed** (▪▫▬▲►▼◄◊○●); many others still to test |
| U+2600–U+26FF | Miscellaneous Symbols | **Partially confirmed** (☺☻☼♀♂♠♣♥♦♪♫); many untested |
| U+2700–U+27BF | Dingbats | **Largely tested** — only ✓ (U+2713) confirmed working; rest of range failed tint test |
| U+20A0–U+20CF | Currency Symbols | **Partially confirmed** (₣₤₧₪€); others to test |
| U+E000–U+E0FF | SE Private Use Area | **U+E033 confirmed** (small grid/dot, tintable). U+E031–U+E060 surveyed in-game — majority are **coloured DDS** controller/UI icons (face buttons, arrows, bars) that ignore font tint. A small number appear white alpha-mask; confirm codepoints before adding. |

---

## General Notes

- SE's Monospace font cell is `aw=24` (advance width), glyph up to `30×42` pixels.
- Block elements U+2580–U+259F and U+2591–U+2593 are particularly reliable as they use standardised pixel fill percentages.
- PUA range (U+E000+) contains SE-specific glyphs not visible in standard Unicode charts — requires in-game testing to discover.
- U+E001–U+E060 are SE-specific controller/UI icons (Xbox/PlayStation face buttons, arrows, bars). Most are coloured DDS and ignore font tint. Only isolated glyphs (e.g. U+E033) are white alpha-mask — test individually.
- Characters with coloured DDS textures (not white alpha-mask) render in a fixed colour and **ignore** font tint — avoid these for tinted/NV output.
- Directional glyphs (corners, arrows) work technically but can leave visible patterns in large ASCII art areas due to their asymmetric shape.
