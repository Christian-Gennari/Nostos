# Audio reader refresh — #797

The `before-*` files are copies of the repository’s earlier audio-reader
captures (2026-09-19): persistent header, duplicate title/author under the
cover, and the former player styling. The `after-*` files were captured on
2026-10-07 in Chromium at device scale factor 1. The before and after captures
use different books; the refreshed player uses a real LibriVox recording of
*Dracula* with cover art from [Project Gutenberg](https://www.gutenberg.org/ebooks/345).

| View | Before | After |
| --- | --- | --- |
| Desktop light, resting | [Before](before-desktop-light-rest.png) | [After](after-desktop-light-rest.png) |
| Phone light, resting | [Before](before-phone-light-rest.png) | [After](after-phone-light-rest.png) |
| Phone landscape, light | [Before](before-phone-landscape-light-rest.png) | [After](after-phone-landscape-light.png) |
| Desktop dark, resting | — | [After](after-desktop-dark-rest.png) |
| Phone dark, resting | — | [After](after-phone-dark-rest.png) |

## Browser journey

The page was served by the real backend with a new temporary SQLite content
root. Only that fixture received the audiobook, cover, progress, and notes.
The recording is listed in the [LibriVox Dracula catalog](https://librivox.org/dracula-by-bram-stoker).

- Captured at **1440×900**, **390×844**, and **844×390**. The audio title and
  author each appeared once, with no horizontal overflow in any viewport.
- The Playback menu stayed inside the desktop and phone viewports. Opening it
  left the player, cover, and controls boxes unchanged.
- Clicked Play and Pause on the real MP3; the clock advanced from `0:20` to
  `0:21`. MediaSession reported `playing` and the book title.
- Changed speed using the `+0.05×` step and the `1.25×` preset; armed a 15-minute
  sleep timer and turned it off; Escape closed the Playback menu.
- Edited the time to `0:20`, skipped forward to `0:35`, then back to `0:20`.
  The backend returned `lastLocation: "20"` after the progress save, and fresh
  reader pages resumed at `0:20`.
- Added and saved `[0:20] visual evidence pass`; the note was present in the
  backend response. Contents and Notes panels use the shared reader surfaces.
- Loading and invalid-media error states were captured over the cover. Both
  retained the same desktop geometry: player `760×828`, cover `335×502.5`, and
  controls `712×159`.

The fixture backend log recorded successful range responses for the MP3
(`206`), progress updates (`PUT /progress`, `200`), and the timestamped note
(`POST /notes`, `201`), matching the browser observations above.

The MP3 has no chapter metadata, so its Contents panel is empty and chapter-end
sleep is unavailable for this recording. The focused AudioReader unit spec
covers chapter jumps and chapter-end sleep behavior.

## Additional captures

- Playback settings: [desktop](after-desktop-light-playback-menu.png),
  [phone](after-phone-light-playback-menu.png)
- Cover states: [loading](after-desktop-light-loading.png),
  [error](after-desktop-light-error.png)
- Panels: [Notes](after-desktop-light-notes.png),
  [Contents](after-desktop-light-contents.png)
