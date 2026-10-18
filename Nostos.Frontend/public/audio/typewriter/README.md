# Typewriter sounds

The optional key sounds of the Studio page (issues #1042, #1065). There are two
packs, one folder each. A writer picks one under the *Typewriter sounds*
switch. Only the chosen pack is fetched, and only after the sounds are turned on.

## `olivetti-lettera-22/`: Typewriter (the default)

A real manual typewriter. Every sample comes from one machine in one session.

- Source: "typewriter-olivetti-lettra-22.wav" by keithpeter,
  <https://freesound.org/people/keithpeter/sounds/123344/>. An Olivetti
  Lettera 22 is fed a sheet, types about 50 words and has the sheet removed.
  The margin bell rings at the end of each line, and the carriage is pushed
  back. Dynamic microphone about 30 cm away, 44.1 kHz, 16-bit mono.
- Licence: **CC0 1.0** (public domain dedication), as stated on that page.
- Cut from Freesound's high-quality preview of the recording (128 kbit/s MP3).

| File | From (seconds into the recording) |
| --- | --- |
| `key-1.mp3` … `key-12.mp3` | Letter strikes at 13.145, 13.891, 14.155, 15.916, 25.225, 34.527, 43.179, 48.355, 50.785, 67.696, 69.806, 98.561 |
| `space.mp3` | 61.550. The space bar moves only the escapement: a soft thunk and its release click, quieter than a letter. |
| `backspace.mp3` | 12.818. A single escapement click. |
| `return.mp3` | 59.955–61.255. The lever, the line feed, the carriage sliding back and its stop (1.3 s). |
| `bell.mp3` | 41.795. The margin bell's decay. The machine's own bell rings at 3071 Hz with a partial at 7461 Hz, which writers found piercing. Its decay envelope is kept, voiced as a soft 1100 Hz ding with a faint octave, an 8 ms attack and 5 dB below the keys. |

Processing: `scripts/typewriter-samples.py` (ffmpeg and numpy) reproduces these
files from the recording. Mono, 44.1 kHz, 70 Hz high-pass and 13 kHz low-pass
(this removes the recording's 19 kHz whine), cut 4 ms before the strike, 2 ms
fade-in, squared fade-out, true peak −3 dB, 64 kbit/s MP3. Letters are levelled
to the loudness of the keyboard pack's keys (within about 1 dB on the same
meter). Space and backspace are 3 dB softer, as on the machine. Keys are about
2 KB each, the return 11 KB and the bell 8 KB.

## `cherry-kc1000/`: Keyboard

A quiet office keyboard. This was the only pack before #1065.

- Source: "Keyboard Soundpack #1 — typing and single keystrokes" by
  unicaegames, <https://opengameart.org/content/keyboard-soundpack-1-typing-and-single-keystrokes>
  (`unicae_games_keyboard_soundpack_1_0.zip`). Recorded on a Cherry KC 1000.
- Licence: **CC0 1.0** (public domain dedication), as stated on that page.
  The pack's own readme says "Free to use however you like."

Files here, from the pack's `Single Keys/`:

| File | From |
| --- | --- |
| `key-1.mp3` … `key-5.mp3` | `keypress-003`, `-008`, `-011`, `-014`, `-021` |
| `space.mp3` | `keypress-007` |
| `return.mp3` | `keypress-017` |
| `backspace.mp3` | `keypress-029` |

Processing (ffmpeg): mono, 44.1 kHz, leading silence trimmed, a 50 ms fade-out,
loudness normalised to −20 LUFS (true peak −3 dB), 0.24 s, 64 kbit/s MP3.

This pack has no bell, so the margin bell is offered only with the typewriter.

---

Scriptorium's own samples were Calmly Writer's recordings and are not used.
