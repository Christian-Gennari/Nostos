# Read aloud

Nostos can read an EPUB to you from the page it is open on. It marks the
sentence being spoken, turns the page by itself and carries on into the next
chapter.

Open a book, tap the page to bring up the toolbar, and press the speaker button.
A small bar appears with previous sentence, play/pause, next sentence, speed and
stop. It stays on screen while the rest of the toolbar is hidden.

## Two kinds of voice

| Voice | Where it runs | Needs | Works with the screen locked |
| --- | --- | --- | --- |
| Device voice | The phone, tablet or computer you read on | Nothing | Usually not. Nostos keeps the screen awake while it reads |
| Natural voice | A speech server | A speech server connected to Nostos | It is built to: it plays as ordinary audio, like an audiobook. Not yet confirmed on every phone |

The device voice is always there and is what Nostos uses until a natural voice
is connected. How it sounds depends on the device.

When a natural voice is connected, Nostos uses it for books in a language the
voice can read and the device voice for every other book. If the natural voice
fails in the middle of a book, reading carries on with the device voice and a
short message says why.

The voice is chosen in the reader, under **View settings → Read aloud voice**.
The choice is remembered on that device.

## Connect a natural voice (SelfHosted)

Nostos talks to any speech server that offers the OpenAI speech route
(`POST /v1/audio/speech`). This guide uses [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M),
an open model, served by [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI).
It runs on your own machine, needs no account or key, and no text leaves it.

### Docker Compose

Add the speech server next to Nostos and point Nostos at it:

```yaml
services:
  nostos:
    image: ghcr.io/christian-gennari/nostos:latest
    environment:
      ReadAloud__Enabled: "true"
      ReadAloud__BaseUrl: "http://kokoro:8880"
    # ...the rest of your existing nostos service

  kokoro:
    # Pin a release tag from the Kokoro-FastAPI project instead of `latest`.
    image: ghcr.io/remsky/kokoro-fastapi-cpu:latest
    restart: unless-stopped
```

Then run `docker compose up -d`. The speech server does not need a published
port: only Nostos calls it.

### Without Docker

Start a speech server, then set two environment variables and restart Nostos:

```bash
export ReadAloud__Enabled=true
export ReadAloud__BaseUrl=http://localhost:8880
```

### Check that it works

**Settings → AI connections → Read aloud voice** shows **Connected** when the
switch is on and an address is set. Then open an EPUB and press the speaker
button. The first sentence takes a moment while the server renders it; the play
button pulses until sound starts.

## Settings

All of these go in the `ReadAloud` section of `appsettings.json`, or as
`ReadAloud__Name` environment variables.

| Setting | Default | What it does |
| --- | --- | --- |
| `Enabled` | `false` | Turns the natural voice on |
| `BaseUrl` | not set | Address of the speech server. `http://host:8880` and `http://host:8880/v1` both work |
| `Model` | `kokoro` | Model name sent to the speech server |
| `ApiKeyEnvironmentVariable` | `NOSTOS_READ_ALOUD_TOKEN` | Name of the environment variable holding a bearer token. Leave it unset for a local server |
| `MaxCharacters` | `1200` | Longest text accepted in one request. The reader sends one sentence at a time |
| `RequestTimeoutSeconds` | `60` | How long to wait for the speech server |
| `CacheDirectory` | a folder under the system temp directory | Where rendered sentences are kept |
| `CacheMaxBytes` | `536870912` (512 MiB) | Size limit of that folder. `0` turns the cache off |

## Languages

The natural voice reads books in these languages. A book declares its language
in its own metadata; a book in any other language, or one that declares none,
is read with the device voice.

| Language | Voices |
| --- | --- |
| English (US) | Heart, Bella, Michael, Fenrir |
| English (UK) | Emma, Isabella, George, Fable |
| Spanish | Dora, Alex |
| French | Siwis |
| Hindi | Alpha, Omega |
| Italian | Sara, Nicola |
| Japanese | Alpha, Kumo |
| Portuguese (Brazil) | Dora, Alex |
| Chinese (Mandarin) | Xiaoxiao, Yunxi |

## What to expect from your hardware

Kokoro runs on a CPU. On a 12-core desktop it rendered speech about three times
faster than it is spoken, measured with the model's ONNX build. Nostos fetches
the next two sentences while the current one plays, so reading is continuous on
a machine that renders faster than real time. On a slower machine there are
pauses between sentences.

Rendered sentences are cached on the server, so going back a page or reading a
chapter again costs nothing the second time.

## Limits

- EPUB only for now.
- The natural voice needs the book's language in its metadata.
- Changing speed is instant: it changes playback, not the rendering.
- With the device voice, most phones stop reading when the screen locks. That
  is a limit of the browser's speech engine, which is why Nostos keeps the
  screen awake while it reads.
