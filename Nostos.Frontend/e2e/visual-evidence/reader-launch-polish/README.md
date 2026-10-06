# Reader launch polish — #759 follow-up

Captures use Chromium at **1440×900** (desktop) and **390×844** (phone), with
`deviceScaleFactor: 1`. Open each PNG at 100% to inspect glyphs and page edges.
Both light and dark themes were inspected. `before-*` uses the merged #764 UI;
`after-*` uses this follow-up.

The EPUB is Project Gutenberg’s public-domain [Dracula](https://www.gutenberg.org/ebooks/345),
opened at Jonathan Harker’s Journal. The PDF is a typeset reading fixture from
the same passage. Selection captures use the repository’s tiny EPUB, with the
assistant opted in and `/api/assistant/status` mocked available; no AI turn is
sent. Baseline PDF chrome was revealed directly on its container because #764
rejects taps on PDF.js’s focusable text layer. Final PDF captures use real taps.

| Surface | Before | After |
| --- | --- | --- |
| EPUB desktop, dark controls | [Before](before-desktop-dark-controls.png) | [After](after-desktop-dark-controls.png) |
| EPUB phone, light controls | [Before](before-mobile-light-controls.png) | [After](after-mobile-light-controls.png) |
| EPUB desktop, resting | [Before](before-desktop-dark-rest.png) | [After](after-desktop-dark-rest.png) |
| EPUB phone, resting | [Before](before-mobile-light-rest.png) | [After](after-mobile-light-rest.png) |
| PDF desktop, dark controls | [Before](before-pdf-desktop-dark-controls.png) | [After](after-pdf-desktop-dark-controls.png) |
| PDF phone, light controls | [Before](before-pdf-mobile-light-controls.png) | [After](after-pdf-mobile-light-controls.png) |

Settings: [desktop dark](after-desktop-dark-settings.png),
[phone light](after-mobile-light-settings.png),
[PDF phone light](after-pdf-mobile-light-settings.png).

Selection: [desktop](after-selection-desktop-dark.png),
[phone](after-selection-mobile-dark.png),
[phone note editor](after-selection-note-mobile-dark.png).

Browser regression coverage also exercises 320×740, 768×1024, and 844×390,
including Cloud’s extra Feedback tool, a long title, both themes, stable page
geometry, real PDF-page taps, and the five-action selection layout with its
contextual assistant handoff.
