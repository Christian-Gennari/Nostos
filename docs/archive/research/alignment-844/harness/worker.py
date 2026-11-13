"""Local transcription worker for alignment measurements. No sockets.

Protocol over stdio, one JSON object per line:
  in : {"path": "/abs/clip.wav", "language": "en" | null}
  out: {"text": ..., "language": ..., "duration": ..., "words": [{word,start,end}]}
Results are cached on disk by audio hash so reruns are deterministic and cheap.
"""
import hashlib, json, os, sys

MODEL = os.environ.get("WHISPER_MODEL", "large-v3-turbo")
CACHE = os.environ.get("WHISPER_CACHE", os.path.join(os.path.dirname(os.path.abspath(__file__)), "cache"))
os.makedirs(CACHE, exist_ok=True)
model = None


def transcribe(path, language):
    global model
    audio = open(path, "rb").read()
    key = hashlib.sha256(audio + (language or "").encode() + MODEL.encode()).hexdigest()
    cached = os.path.join(CACHE, key + ".json")
    if os.path.exists(cached):
        return json.load(open(cached))
    if model is None:
        from faster_whisper import WhisperModel
        model = WhisperModel(MODEL, device=os.environ.get("WHISPER_DEVICE", "cuda"),
                             compute_type=os.environ.get("WHISPER_COMPUTE", "int8"))
    import numpy as np, wave
    with wave.open(path, "rb") as w:
        assert w.getframerate() == 16000 and w.getnchannels() == 1 and w.getsampwidth() == 2
        samples = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
    segments, info = model.transcribe(
        samples, language=language or None, word_timestamps=True,
        beam_size=5, condition_on_previous_text=False, vad_filter=False)
    words, text = [], ""
    for s in segments:
        text += s.text
        for w in s.words or []:
            words.append({"word": w.word, "start": w.start, "end": w.end})
    result = {"text": text.strip(), "language": info.language,
              "duration": info.duration, "words": words}
    json.dump(result, open(cached, "w"), ensure_ascii=False)
    return result


print(json.dumps({"ready": MODEL}), flush=True)
for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        request = json.loads(line)
        language = request.get("language")
        if language:
            language = language.split("-")[0].lower()
        out = transcribe(request["path"], language)
    except Exception as e:  # noqa
        out = {"error": str(e)}
    print(json.dumps(out, ensure_ascii=False), flush=True)
