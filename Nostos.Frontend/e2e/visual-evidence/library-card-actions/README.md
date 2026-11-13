# Library grid card — quick action buttons do not take clicks

Evidence for the fix to the four buttons that sit on a Library grid card's
cover: finished (top-left), favourite (top-right), edit and delete (centre, on
hover). Captured from the production build (`npm run build`) served by
`npm run preview -- serve`, at DPR 1.

## Root cause (measured, not inferred)

- The card's title is a real link whose `::after` stretches over the whole card
  (`inset: 0; z-index: 0`), added in #1376 so Enter/Space opens a card.
- `.cover-wrapper` carries `transform: translateZ(0)` to stop paint flashing.
  A non-`none` transform makes it a stacking context, so the buttons inside it
  (`.finished-btn-grid` / `.fav-btn-grid` at `z-index: 2`, `.actions-overlay`
  at `z-index: 1`) are atomic: none of them can paint, or be hit, above the
  link layer outside that context.
- `document.elementFromPoint()` at the centre of each of the four discs
  returned `a.card-link` (before the fix) instead of the button. Playwright's
  click on Edit timed out with "element intercepts pointer events"; clicking a
  disc navigated nowhere and toggled nothing. Verified on the current build at
  1280px, light and dark.

## Fix

`.book-card` isolates its layers (`isolation: isolate`); `.cover-wrapper` is
lifted above the link (`z-index: 1`) and made pointer-transparent
(`pointer-events: none`), with the pointer restored on the controls only while
the card reveals them (`:hover` / `:focus-within`), and always on the
select-mode checkbox. The art therefore still reaches the card link
underneath — clicking the cover opens the book exactly as before — while the
four discs and the checkbox are real targets again. The hover overlay's
ground stays pointer-transparent too, so its gap does not become a dead zone.

## Verified after the fix

- At rest `elementFromPoint` at each disc centre returns `a.card-link`; on
  hover it returns the button's `svg`. No invisible control can fire.
- Edit opens the "Edit book" dialog; Delete opens the confirm; finished and
  favourite each send `PUT /api/books/{id}` (`{"isFinished":true}` /
  `{"isFavorite":true}`); clicking the cover art — or the hover overlay's
  ground between the discs — still opens the book.
- In Select mode the card's checkbox toggles; at 390px the hover actions stay
  hidden and tapping the cover opens the book.
- `npm run check` clean.

## Images

- `after-card-hover-1280-{light,dark}.png` — the four discs after the fix, at
  1:1, from the production build.
- `after-edit-from-card-1280-{light,dark}.png` — the editor opened by clicking
  the card's pencil. This is the click that previously never reached the
  button.
