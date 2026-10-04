/**
 * Nostos icon registry — the ONE place that maps a glyph name to an icon asset.
 *
 * WHY THIS FILE EXISTS
 * --------------------
 * Feature code never imports an icon library. It asks for a NAME (`<nostos-icon
 * name="trash" />`, or a semantic concept) and this registry resolves it. An icon
 * family swap therefore touches this file, `nostos-icon.component.ts` and
 * `angular.json` — not 20 templates.
 *
 * WHY `@phosphor-icons/core` (and not `@phosphor-icons/webcomponents`)
 * -------------------------------------------------------------------
 * Both are official Phosphor packages. Measured before choosing:
 *
 *   - `webcomponents@2.1.5` ships one ES module per icon, but each module inlines
 *     ALL SIX weights (~4.8 kB/icon, 7.3 MB for the set) and pulls in the vendored
 *     lit runtime (25 kB of `@lit/reactive-element` + `lit-html` + `lit-element`,
 *     reached through `.pnpm/` paths inside its own `dist/`). Importing n icons
 *     pays n x 4.8 kB whether or not those weights are used, plus a shadow-DOM
 *     custom element per glyph that Angular only accepts via CUSTOM_ELEMENTS_SCHEMA
 *     and that CSS cannot reach into.
 *   - `core@2.1.1` is the SVG asset catalog (505 B per regular glyph, 453 B per
 *     variant). This registry imports exactly the files the app renders, so the
 *     bundle carries exactly those: **98 files / 68 glyphs**, no runtime dependency
 *     and no shadow DOM. The cost of that choice is one line of build wiring —
 *     `"loader": { ".svg": "text" }` in `angular.json` — and the module declaration
 *     in `phosphor-svg.d.ts`.
 *
 * Both satisfy the issue's "no whole-library global load"; `core` satisfies it by
 * a factor of ~10 on bytes and keeps the icons styleable from ordinary Nostos CSS.
 *
 * ONE BUILD-COUPLING TO KNOW ABOUT. Adding ANY custom loader makes Angular's
 * esbuild pipeline drop its `packages: 'external'` shortcut (see
 * `@angular/build/src/tools/esbuild/external-packages-plugin.js`), which is what
 * used to externalise bare `node:` imports in the test build. The two specs that
 * read files through `node:fs` / `node:path` / `node:process` therefore stopped
 * resolving, so those three are listed in the build target's
 * `externalDependencies` in `angular.json`. The `.svg` loader and that list come
 * as a pair: remove either and `npm test` fails.
 *
 * ADDING GLYPHS AND WEIGHTS
 * -------------------------
 * Add imports here (that is what makes them bundle), then name them in
 * `NOSTOS_ICONS`. `regular` is MANDATORY per glyph: it is the fallback the
 * component resolves to when a requested weight was not imported. A weight that is
 * not imported is not a typo-safe value anywhere — it silently degrades to
 * regular, so import the variant rather than relying on the fallback.
 *
 * Naming: Phosphor's own kebab-case glyph names (`book-open`, `caret-down`,
 * `magnifying-glass`). Do not invent Nostos aliases for glyphs; semantic Nostos
 * vocabulary lives in `nostos-concepts.ts`.
 */

import pauseFill from '@phosphor-icons/core/fill/pause-fill.svg';
import playFill from '@phosphor-icons/core/fill/play-fill.svg';
import starFill from '@phosphor-icons/core/fill/star-fill.svg';
import archiveLight from '@phosphor-icons/core/light/archive-light.svg';
import bookLight from '@phosphor-icons/core/light/book-light.svg';
import bookOpenLight from '@phosphor-icons/core/light/book-open-light.svg';
import booksLight from '@phosphor-icons/core/light/books-light.svg';
import brainLight from '@phosphor-icons/core/light/brain-light.svg';
import checkCircleLight from '@phosphor-icons/core/light/check-circle-light.svg';
import circleDashedLight from '@phosphor-icons/core/light/circle-dashed-light.svg';
import dotsThreeLight from '@phosphor-icons/core/light/dots-three-light.svg';
import fileTextLight from '@phosphor-icons/core/light/file-text-light.svg';
import folderLight from '@phosphor-icons/core/light/folder-light.svg';
import folderOpenLight from '@phosphor-icons/core/light/folder-open-light.svg';
import gearSixLight from '@phosphor-icons/core/light/gear-six-light.svg';
import headphonesLight from '@phosphor-icons/core/light/headphones-light.svg';
import heartLight from '@phosphor-icons/core/light/heart-light.svg';
import listBulletsLight from '@phosphor-icons/core/light/list-bullets-light.svg';
import mapTrifoldLight from '@phosphor-icons/core/light/map-trifold-light.svg';
import paletteLight from '@phosphor-icons/core/light/palette-light.svg';
import paperPlaneTiltLight from '@phosphor-icons/core/light/paper-plane-tilt-light.svg';
import penNibLight from '@phosphor-icons/core/light/pen-nib-light.svg';
import plusLight from '@phosphor-icons/core/light/plus-light.svg';
import sparkleLight from '@phosphor-icons/core/light/sparkle-light.svg';
import squaresFourLight from '@phosphor-icons/core/light/squares-four-light.svg';
import trayLight from '@phosphor-icons/core/light/tray-light.svg';
import uploadSimpleLight from '@phosphor-icons/core/light/upload-simple-light.svg';
import alignCenterVertical from '@phosphor-icons/core/regular/align-center-vertical.svg';
import archive from '@phosphor-icons/core/regular/archive.svg';
import arrowClockwise from '@phosphor-icons/core/regular/arrow-clockwise.svg';
import arrowCounterClockwise from '@phosphor-icons/core/regular/arrow-counter-clockwise.svg';
import arrowLeft from '@phosphor-icons/core/regular/arrow-left.svg';
import arrowLineLeft from '@phosphor-icons/core/regular/arrow-line-left.svg';
import arrowLineRight from '@phosphor-icons/core/regular/arrow-line-right.svg';
import arrowRight from '@phosphor-icons/core/regular/arrow-right.svg';
import arrowSquareOut from '@phosphor-icons/core/regular/arrow-square-out.svg';
import arrowUUpLeft from '@phosphor-icons/core/regular/arrow-u-up-left.svg';
import arrowsClockwise from '@phosphor-icons/core/regular/arrows-clockwise.svg';
import arrowsIn from '@phosphor-icons/core/regular/arrows-in.svg';
import arrowsOut from '@phosphor-icons/core/regular/arrows-out.svg';
import bookOpen from '@phosphor-icons/core/regular/book-open.svg';
import book from '@phosphor-icons/core/regular/book.svg';
import bookmarkSimple from '@phosphor-icons/core/regular/bookmark-simple.svg';
import books from '@phosphor-icons/core/regular/books.svg';
import brain from '@phosphor-icons/core/regular/brain.svg';
import buildings from '@phosphor-icons/core/regular/buildings.svg';
import calendarBlank from '@phosphor-icons/core/regular/calendar-blank.svg';
import caretDown from '@phosphor-icons/core/regular/caret-down.svg';
import caretLeft from '@phosphor-icons/core/regular/caret-left.svg';
import caretRight from '@phosphor-icons/core/regular/caret-right.svg';
import caretUp from '@phosphor-icons/core/regular/caret-up.svg';
import checkCircle from '@phosphor-icons/core/regular/check-circle.svg';
import check from '@phosphor-icons/core/regular/check.svg';
import circleDashed from '@phosphor-icons/core/regular/circle-dashed.svg';
import circleNotch from '@phosphor-icons/core/regular/circle-notch.svg';
import clock from '@phosphor-icons/core/regular/clock.svg';
import copy from '@phosphor-icons/core/regular/copy.svg';
import cornersIn from '@phosphor-icons/core/regular/corners-in.svg';
import cornersOut from '@phosphor-icons/core/regular/corners-out.svg';
import crosshair from '@phosphor-icons/core/regular/crosshair.svg';
import dotsThree from '@phosphor-icons/core/regular/dots-three.svg';
import downloadSimple from '@phosphor-icons/core/regular/download-simple.svg';
import fileText from '@phosphor-icons/core/regular/file-text.svg';
import folderOpen from '@phosphor-icons/core/regular/folder-open.svg';
import folderPlus from '@phosphor-icons/core/regular/folder-plus.svg';
import folderSimple from '@phosphor-icons/core/regular/folder-simple.svg';
import folder from '@phosphor-icons/core/regular/folder.svg';
import gearSix from '@phosphor-icons/core/regular/gear-six.svg';
import gitMerge from '@phosphor-icons/core/regular/git-merge.svg';
import globe from '@phosphor-icons/core/regular/globe.svg';
import hash from '@phosphor-icons/core/regular/hash.svg';
import headphones from '@phosphor-icons/core/regular/headphones.svg';
import heart from '@phosphor-icons/core/regular/heart.svg';
import highlighter from '@phosphor-icons/core/regular/highlighter.svg';
import image from '@phosphor-icons/core/regular/image.svg';
import info from '@phosphor-icons/core/regular/info.svg';
import linkSimple from '@phosphor-icons/core/regular/link-simple.svg';
import listBullets from '@phosphor-icons/core/regular/list-bullets.svg';
import list from '@phosphor-icons/core/regular/list.svg';
import magnifyingGlass from '@phosphor-icons/core/regular/magnifying-glass.svg';
import mapPin from '@phosphor-icons/core/regular/map-pin.svg';
import mapTrifold from '@phosphor-icons/core/regular/map-trifold.svg';
import microphone from '@phosphor-icons/core/regular/microphone.svg';
import minus from '@phosphor-icons/core/regular/minus.svg';
import moon from '@phosphor-icons/core/regular/moon.svg';
import note from '@phosphor-icons/core/regular/note.svg';
import notebook from '@phosphor-icons/core/regular/notebook.svg';
import palette from '@phosphor-icons/core/regular/palette.svg';
import paperPlaneTilt from '@phosphor-icons/core/regular/paper-plane-tilt.svg';
import pause from '@phosphor-icons/core/regular/pause.svg';
import penNib from '@phosphor-icons/core/regular/pen-nib.svg';
import pencilSimple from '@phosphor-icons/core/regular/pencil-simple.svg';
import play from '@phosphor-icons/core/regular/play.svg';
import plus from '@phosphor-icons/core/regular/plus.svg';
import quotes from '@phosphor-icons/core/regular/quotes.svg';
import scan from '@phosphor-icons/core/regular/scan.svg';
import sidebarSimple from '@phosphor-icons/core/regular/sidebar-simple.svg';
import slidersHorizontal from '@phosphor-icons/core/regular/sliders-horizontal.svg';
import sparkle from '@phosphor-icons/core/regular/sparkle.svg';
import squaresFour from '@phosphor-icons/core/regular/squares-four.svg';
import stack from '@phosphor-icons/core/regular/stack.svg';
import star from '@phosphor-icons/core/regular/star.svg';
import stop from '@phosphor-icons/core/regular/stop.svg';
import sun from '@phosphor-icons/core/regular/sun.svg';
import textT from '@phosphor-icons/core/regular/text-t.svg';
import trash from '@phosphor-icons/core/regular/trash.svg';
import tray from '@phosphor-icons/core/regular/tray.svg';
import uploadSimple from '@phosphor-icons/core/regular/upload-simple.svg';
import warningCircle from '@phosphor-icons/core/regular/warning-circle.svg';
import warning from '@phosphor-icons/core/regular/warning.svg';
import waveform from '@phosphor-icons/core/regular/waveform.svg';
import x from '@phosphor-icons/core/regular/x.svg';
import alignCenterVerticalThin from '@phosphor-icons/core/thin/align-center-vertical-thin.svg';
import arrowLineLeftThin from '@phosphor-icons/core/thin/arrow-line-left-thin.svg';
import arrowLineRightThin from '@phosphor-icons/core/thin/arrow-line-right-thin.svg';
import bookOpenThin from '@phosphor-icons/core/thin/book-open-thin.svg';
import bookThin from '@phosphor-icons/core/thin/book-thin.svg';
import brainThin from '@phosphor-icons/core/thin/brain-thin.svg';
import cornersInThin from '@phosphor-icons/core/thin/corners-in-thin.svg';
import cornersOutThin from '@phosphor-icons/core/thin/corners-out-thin.svg';
import folderPlusThin from '@phosphor-icons/core/thin/folder-plus-thin.svg';
import plusThin from '@phosphor-icons/core/thin/plus-thin.svg';

/**
 * Phosphor's weight vocabulary. `regular` is the system default (see the issue's
 * icon language); `fill` is for a selected/active state where a solid glyph reads
 * better; `duotone`, `bold`, `light` and `thin` are deliberate, per-glyph extras.
 * A weight is only usable once its asset is imported below.
 */
export type NostosIconWeight = 'regular' | 'thin' | 'light' | 'bold' | 'fill' | 'duotone';

/** One glyph's available weights. `regular` is required; the rest are extras. */
export type NostosIconVariants = { readonly regular: string } & {
  readonly [W in Exclude<NostosIconWeight, 'regular'>]?: string;
};

/**
 * Glyph name -> inline SVG markup per weight. `as const` keeps the names literal
 * so `NostosIconName` is a real union and `<nostos-icon name="...">` is checked at
 * compile time (a typo is a build error, not a blank box).
 */
export const NOSTOS_ICONS = {
  'align-center-vertical': { regular: alignCenterVertical, thin: alignCenterVerticalThin, },
  'archive': { regular: archive, light: archiveLight, },
  'arrow-clockwise': { regular: arrowClockwise, },
  'arrow-counter-clockwise': { regular: arrowCounterClockwise, },
  'arrow-left': { regular: arrowLeft, },
  'arrow-line-left': { regular: arrowLineLeft, thin: arrowLineLeftThin, },
  'arrow-line-right': { regular: arrowLineRight, thin: arrowLineRightThin, },
  'arrow-right': { regular: arrowRight, },
  'arrow-square-out': { regular: arrowSquareOut, },
  'arrow-u-up-left': { regular: arrowUUpLeft, },
  'arrows-clockwise': { regular: arrowsClockwise, },
  'arrows-in': { regular: arrowsIn, },
  'arrows-out': { regular: arrowsOut, },
  'book': { regular: book, thin: bookThin, light: bookLight, },
  'book-open': { regular: bookOpen, thin: bookOpenThin, light: bookOpenLight, },
  'bookmark-simple': { regular: bookmarkSimple, },
  'books': { regular: books, light: booksLight, },
  'brain': { regular: brain, thin: brainThin, light: brainLight, },
  'buildings': { regular: buildings, },
  'calendar-blank': { regular: calendarBlank, },
  'caret-down': { regular: caretDown, },
  'caret-left': { regular: caretLeft, },
  'caret-right': { regular: caretRight, },
  'caret-up': { regular: caretUp, },
  'check': { regular: check, },
  'check-circle': { regular: checkCircle, light: checkCircleLight, },
  'circle-dashed': { regular: circleDashed, light: circleDashedLight, },
  'circle-notch': { regular: circleNotch, },
  'clock': { regular: clock, },
  'copy': { regular: copy, },
  'corners-in': { regular: cornersIn, thin: cornersInThin, },
  'corners-out': { regular: cornersOut, thin: cornersOutThin, },
  'crosshair': { regular: crosshair, },
  'dots-three': { regular: dotsThree, light: dotsThreeLight, },
  'download-simple': { regular: downloadSimple, },
  'file-text': { regular: fileText, light: fileTextLight, },
  'folder': { regular: folder, light: folderLight, },
  'folder-open': { regular: folderOpen, light: folderOpenLight, },
  'folder-plus': { regular: folderPlus, thin: folderPlusThin, },
  'folder-simple': { regular: folderSimple, },
  'gear-six': { regular: gearSix, light: gearSixLight, },
  'git-merge': { regular: gitMerge, },
  'globe': { regular: globe, },
  'hash': { regular: hash, },
  'headphones': { regular: headphones, light: headphonesLight, },
  'heart': { regular: heart, light: heartLight, },
  'highlighter': { regular: highlighter, },
  'image': { regular: image, },
  'info': { regular: info, },
  'link-simple': { regular: linkSimple, },
  'list': { regular: list, },
  'list-bullets': { regular: listBullets, light: listBulletsLight, },
  'magnifying-glass': { regular: magnifyingGlass, },
  'map-pin': { regular: mapPin, },
  'map-trifold': { regular: mapTrifold, light: mapTrifoldLight, },
  'microphone': { regular: microphone, },
  'minus': { regular: minus, },
  'moon': { regular: moon, },
  'note': { regular: note, },
  'notebook': { regular: notebook, },
  'palette': { regular: palette, light: paletteLight, },
  'paper-plane-tilt': { regular: paperPlaneTilt, light: paperPlaneTiltLight, },
  'pause': { regular: pause, fill: pauseFill, },
  'pen-nib': { regular: penNib, light: penNibLight, },
  'pencil-simple': { regular: pencilSimple, },
  'play': { regular: play, fill: playFill, },
  'plus': { regular: plus, thin: plusThin, light: plusLight, },
  'quotes': { regular: quotes, },
  'scan': { regular: scan, },
  'sidebar-simple': { regular: sidebarSimple, },
  'sliders-horizontal': { regular: slidersHorizontal, },
  'sparkle': { regular: sparkle, light: sparkleLight, },
  'squares-four': { regular: squaresFour, light: squaresFourLight, },
  'stack': { regular: stack, },
  'star': { regular: star, fill: starFill, },
  'stop': { regular: stop, },
  'sun': { regular: sun, },
  'text-t': { regular: textT, },
  'trash': { regular: trash, },
  'tray': { regular: tray, light: trayLight, },
  'upload-simple': { regular: uploadSimple, light: uploadSimpleLight, },
  'warning': { regular: warning, },
  'warning-circle': { regular: warningCircle, },
  'waveform': { regular: waveform, },
  'x': { regular: x, },
} as const satisfies Record<string, NostosIconVariants>;

/** Every glyph this app ships. Type-safe, literal, and closed on purpose. */
export type NostosIconName = keyof typeof NOSTOS_ICONS;

/** Runtime list, for tests and for callers that iterate rather than name. */
export const NOSTOS_ICON_NAMES = Object.keys(NOSTOS_ICONS) as NostosIconName[];

/**
 * The shared glyph-size rungs. New code picks from these; the migration itself
 * kept every call site's existing measured pixel value (the sizes actually in use
 * across the app are 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40 and 48,
 * with 16 the mode at 42 call sites). `size` defaults to `16`.
 */
export const NOSTOS_ICON_SIZE = { sm: 14, md: 16, lg: 20, xl: 24 } as const;
