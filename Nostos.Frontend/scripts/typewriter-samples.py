"""Cut the Studio's typewriter samples (issue #1065) from one recording.

Source: "typewriter-olivetti-lettra-22.wav" by keithpeter, CC0,
<https://freesound.org/people/keithpeter/sounds/123344/>. Times below are
seconds into that recording. See public/audio/typewriter/README.md.

Usage (needs ffmpeg with libmp3lame, and numpy):
  python3 scripts/typewriter-samples.py <recording> public/audio/typewriter/olivetti-lettera-22 [<reference.mp3>]

With a reference sample, prints its loudness on the same meter, to compare levels.
"""
import json, subprocess, sys
import numpy as np

src, out = sys.argv[1], sys.argv[2]
sr = 44100

# name: (start s, length s, fade-out s, loudness target LUFS)
CUTS = {
    'key-1': (13.145, 0.20, 0.05, -20),
    'key-2': (13.891, 0.20, 0.05, -20),
    'key-3': (14.155, 0.20, 0.05, -20),
    'key-4': (15.916, 0.20, 0.05, -20),
    'key-5': (25.225, 0.20, 0.05, -20),
    'key-6': (34.527, 0.20, 0.05, -20),
    'key-7': (43.179, 0.20, 0.05, -20),
    'key-8': (48.355, 0.20, 0.05, -20),
    'key-9': (50.785, 0.20, 0.05, -20),
    'key-10': (67.696, 0.20, 0.05, -20),
    'key-11': (69.806, 0.20, 0.05, -20),
    'key-12': (98.561, 0.20, 0.05, -20),
    # The space bar and backspace move only the escapement: a softer thunk.
    'space': (61.550, 0.32, 0.06, -23),
    'backspace': (12.818, 0.24, 0.05, -23),
    # Lever, line feed, the carriage sliding back and its stop.
    'return': (59.955, 1.30, 0.15, -20),
}
BELL = (41.795, 0.95, -22)


def decode(path):
    raw = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, '-ac', '1', '-ar', str(sr), '-f', 'f32le', '-'],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).copy()


def ffilter(y, chain):
    r = subprocess.run(['ffmpeg', '-v', 'error', '-f', 'f32le', '-ar', str(sr), '-ac', '1', '-i', '-',
                        '-af', chain, '-f', 'f32le', '-'], input=y.astype(np.float32).tobytes(),
                       capture_output=True, check=True)
    return np.frombuffer(r.stdout, dtype=np.float32).copy()


def lufs(y):
    """Integrated loudness of a short clip: K-weighted mean square (one block)."""
    k = ffilter(y, 'highshelf=f=1500:g=4:t=q:w=0.7,highpass=f=38:poles=2')
    ms = float((k ** 2).mean())
    return -0.691 + 10 * np.log10(ms + 1e-12)


def finish(y, fade_out, target):
    n_in = int(0.002 * sr)
    n_out = int(fade_out * sr)
    y = y.copy()
    y[:n_in] *= np.linspace(0, 1, n_in)
    y[-n_out:] *= np.linspace(1, 0, n_out) ** 2
    y *= 10 ** ((target - lufs(y)) / 20)
    peak = np.abs(y).max()
    ceiling = 10 ** (-3 / 20)
    if peak > ceiling:  # true peak −3 dB: soft-limit the transient rather than drop the level
        y = np.tanh(y / ceiling) * ceiling
    return y


def encode(y, name):
    subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 'f32le', '-ar', str(sr), '-ac', '1', '-i', '-',
                    '-c:a', 'libmp3lame', '-b:a', '64k', '-map_metadata', '-1', '-write_xing', '0',
                    f'{out}/{name}.mp3'], input=y.astype(np.float32).tobytes(), check=True)


x = decode(src)
# Rumble and the recording's 19 kHz whine go; the strike itself lives below 12 kHz.
clean = ffilter(x, 'highpass=f=70,lowpass=f=13000')
report = {}
for name, (start, length, fade, target) in CUTS.items():
    a = int((start - 0.004) * sr)
    y = finish(clean[a:a + int(length * sr)], fade, target)
    encode(y, name)
    report[name] = round(lufs(y), 1)

# The bell rings over the key that tripped it, so keep only its two partials.
start, length, target = BELL
a = int(start * sr)
seg = x[a:a + int(length * sr)]
bell = (ffilter(seg, 'bandpass=f=3071:width_type=h:w=40,bandpass=f=3071:width_type=h:w=40')
        + 0.5 * ffilter(seg, 'bandpass=f=7461:width_type=h:w=60,bandpass=f=7461:width_type=h:w=60'))
# Later keys still leak through the filters as bumps; a struck bell only decays,
# so hold the envelope to its running minimum after the peak.
win = int(0.008 * sr)
env = np.convolve(np.abs(bell), np.ones(win) / win, mode='same') + 1e-7
p = int(np.argmax(env))
target_env = env.copy()
target_env[p:] = np.minimum.accumulate(env[p:])
bell = bell * np.minimum(1, target_env / env)
y = finish(bell, 0.35, target)
encode(y, 'bell')
report['bell'] = round(lufs(y), 1)

if len(sys.argv) > 3:
    report['reference'] = round(lufs(decode(sys.argv[3])), 1)
print(json.dumps(report, indent=1))
