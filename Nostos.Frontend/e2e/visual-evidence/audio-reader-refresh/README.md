# Audio reader refresh — #797

The `before-*` captures were recaptured on 2026-10-07 from a detached checkout
of `main` at `b214a413` (PR #800's base at capture time). The before and after
resting views use the same Dracula fixture: *Dracula: Chapter 1*, Bram Stoker,
the same LibriVox MP3, and the same Project Gutenberg cover. The feature branch
captures use the refreshed reader. All screenshots are Chromium at device
scale factor 1. All three resting comparisons show `0:00`. MP3 SHA-256:
`f66facbcac53128d6f38200de32f5e70a0df3cd50520d62c033e606b2567bae7`; cover
SHA-256: `16f3f822214a011e4df321b7bf10c3cdf20cfb31fa699463f323191a38c05d`.

| View | Before (`main`) | After (PR branch) |
| --- | --- | --- |
| Desktop light, resting — 1440×900 | [Before](before-desktop-light-rest.png) | [After](after-desktop-light-rest.png) |
| Phone light, resting — 390×844 | [Before](before-phone-light-rest.png) | [After](after-phone-light-rest.png) |
| Phone landscape, light — 844×390 | [Before](before-phone-landscape-light-rest.png) | [After](after-phone-landscape-light.png) |
| Desktop dark, resting — 1440×900 | — | [After](after-desktop-dark-rest.png) |
| Phone dark, resting — 390×844 | — | [After](after-phone-dark-rest.png) |

The baseline displayed the title and author twice (edge header and below the
cover); the refreshed resting views display each once.

## Browser journey

The PR branch ran with the real Nostos backend and a fresh temporary SQLite
content root. The isolated library contained the Dracula MP3/cover and a
synthetic 24-second M4B fixture with three embedded chapter markers. The
temporary database was removed after the run; the fixture backend log was
retained under the ignored `e2e/test-results/` directory. No user library data
was accessed.

The Dracula recording is listed in the
[LibriVox catalog](https://librivox.org/dracula-by-bram-stoker), and the cover
is from [Project Gutenberg](https://www.gutenberg.org/ebooks/345).

- Captured the same Dracula resting view at **1440×900**, **390×844**, and
  **844×390** on `main` and the PR branch. The refreshed title and author each
  appeared once, with no horizontal overflow.
- The Playback menu stayed in the desktop and phone viewports. Opening it left
  the player, cover, and controls boxes unchanged. The dark phone menu at
  **390×844** measured `358×352` at `(16, 195)` and remained in bounds.
- Clicked Play and Pause on the real MP3; the clock advanced from `0:20` to
  `0:21`. MediaSession reported `playing` and the book title.
- Changed speed using the `+0.05×` step and `1.25×` preset; armed a 15-minute
  sleep timer and turned it off; Escape closed the Playback menu.
- Edited the time to `0:20`, skipped forward to `0:35`, then back to `0:20`.
  The backend returned `lastLocation: "20"` after the progress save, and a fresh
  reader page resumed at `0:20`.
- Added and saved `[0:20] visual evidence pass`; the note was present in the
  backend response. Contents and Notes use the shared reader panel surfaces.
- Loading and invalid-media error states stayed over the cover without moving
  the player. Desktop geometry remained player `760×828`, cover `335×502.5`,
  controls `712×159`.

### Chapters

The test M4B was generated with ffmpeg and uploaded through the ordinary book
file endpoint. `ffprobe` and the app's upload metadata path both reported:

| Chapter | Start |
| --- | ---: |
| Chapter One | 0 s |
| Chapter Two | 8 s |
| Chapter Three | 16 s |

- The Contents panel listed all three chapters on desktop and phone. Captures:
  [desktop light](after-desktop-light-chapters.png) and
  [phone light](after-phone-light-chapters.png).
- Clicking Chapter Three moved the audio position to `0:16`; clicking Chapter
  Two moved it to `0:08`.
- The Playback menu showed **Chapter end**. Starting it from Chapter Two paused
  at the 16-second boundary and displayed “Chapter finished. Playback paused.”
  The displayed time was `0:16`; the persisted progress was `16.932811` seconds
  because the reader checks chapter boundaries on its one-second progress tick.
  A fresh reader page resumed at `0:16`. The paused state is captured in
  [this screenshot](after-desktop-light-chapter-end-paused.png).
- The backend log recorded the M4B range request (`206`) and progress writes
  (`PUT /progress`, `200`). The book GET returned all three chapter starts and
  the persisted playback position.

The chapter audio itself is a generated tone, not a full-length commercial
audiobook; this validates embedded M4B chapter ingestion and reader behavior.
Browser coverage used desktop Chromium and emulated phone viewports; native
iOS/Android browser and hardware media controls were not exercised.

## Additional captures

- Playback settings: [desktop light](after-desktop-light-playback-menu.png),
  [phone light](after-phone-light-playback-menu.png), and
  [phone dark](after-phone-dark-playback-menu.png)
- Cover states: [loading](after-desktop-light-loading.png),
  [error](after-desktop-light-error.png)
- Panels: [Notes](after-desktop-light-notes.png),
  [Contents without chapters](after-desktop-light-contents.png)
