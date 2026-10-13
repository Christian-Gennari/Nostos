# Current chapter on the audio reader — #904

Chromium at device scale factor 1, from the production build, served by the
real backend on a fresh temporary SQLite content root (the e2e fixture
launcher). No user library data was read. The change is the same bundle on
SelfHosted and Cloud; these are SelfHosted.

Two synthetic 20-minute fixtures made with ffmpeg and uploaded through the
ordinary book file endpoint: an M4B with four embedded chapters (0:00, 5:00,
10:00, 15:00; the second has a 131-character LibriVox-style title) and an MP3
with none.

| View | Chapter | Long title | No chapters | Contents opened from the line |
| --- | --- | --- | --- | --- |
| Desktop 1440×900, light | [shot](audio-chapter-desktop-light.png) | [shot](audio-chapter-long-desktop-light.png) | [shot](audio-chapter-none-desktop-light.png) | [shot](audio-chapter-contents-desktop-light.png) |
| Desktop 1440×900, dark | [shot](audio-chapter-desktop-dark.png) | [shot](audio-chapter-long-desktop-dark.png) | [shot](audio-chapter-none-desktop-dark.png) | [shot](audio-chapter-contents-desktop-dark.png) |
| Phone 390×844, light | [shot](audio-chapter-phone-light.png) | [shot](audio-chapter-long-phone-light.png) | [shot](audio-chapter-none-phone-light.png) | [shot](audio-chapter-contents-phone-light.png) |
| Phone 390×844, dark | [shot](audio-chapter-phone-dark.png) | [shot](audio-chapter-long-phone-dark.png) | [shot](audio-chapter-none-phone-dark.png) | [shot](audio-chapter-contents-phone-dark.png) |
| Phone landscape 844×390, light | [shot](audio-chapter-landscape-light.png) | [shot](audio-chapter-long-landscape-light.png) | [shot](audio-chapter-none-landscape-light.png) | — |
| Phone landscape 844×390, dark | [shot](audio-chapter-landscape-dark.png) | [shot](audio-chapter-long-landscape-dark.png) | [shot](audio-chapter-none-landscape-dark.png) | — |

## Measured

[`results.txt`](results.txt) is the output of the pass: 75 checks, 0 failures.

- **Same geometry with and without chapters.** Cover, scrubber, play button,
  Playback pill and controls boxes are equal between the two books in all six
  views (desktop: cover `335×502.5` at `(552.5, 121.25)`, scrubber at
  `y 687.75`, play at `y 774.75`). No vertical scroll, no sideways scroll.
- **No layout shift.** The same boxes are unchanged after the label changes to
  the long title and after a Contents jump.
- **Without chapters.** No chapter line in the DOM. The only rule the change
  adds that reaches that page is `position: relative` on `.controls`; a
  screenshot with it reverted in the page is byte-identical in all six views.
  That is a comparison inside this build, not against a build of `main`.
- **Long title.** One 44px line, ellipsis, the button no wider than the
  controls (`712`, `358`, `591`px), full title in `title`.
- **Contents.** Clicking the line opens Contents with the active row inside the
  list's viewport; choosing another chapter updates the line. Reached and
  activated from the keyboard; closing Contents returns focus to the line.
- **Playback.** Played the real file from `9:56` across the `10:00` chapter
  start: the line changed to *Book III: The Duel*. Reloading resumed in the same
  chapter from the saved position (`lastLocation 603.93`).
- **Media Session**, read back from `navigator.mediaSession.metadata` in
  headless Chromium: `Book I: The Quarrel` / `Homer` / `The Iliad` at load, and
  exactly one write per chapter change across the seek and the boundary. The
  unchaptered book keeps `The Odyssey` / `Homer`. **Not checked on a lock screen
  or in Chrome's media hub.**
