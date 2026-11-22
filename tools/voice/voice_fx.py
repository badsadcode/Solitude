"""Solitude voice treatment: a clean TTS line -> a distress loop heard on the boat's comms set.

  radio   : narrow band (350-2800 Hz), soft-clipped, crackle, slow signal fade, brief dropouts, a carrier hum,
            and a squelch tail. The way a 40-year-old transceiver on a wreck sounds through 600 m of water.
  hull    : the same, then low-passed and smeared, as if heard through the pressure hull (for the hydrophone).

  clip    : radio without the lead-in, tail and squelch, for short pieces the game strings together.

usage (standalone): python voice_fx.py in.wav out_radio.wav out_hull.wav [seed]
Used by generate.py: radio(x, sr, rng, clip=False).
"""
import sys
import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, sosfilt

def load(path):
    sr, x = wavfile.read(path)
    x = x.astype(np.float32)
    if x.ndim > 1: x = x.mean(axis=1)
    x /= max(1e-6, np.abs(x).max())   # bring quiet takes (whispers) up to level first
    return sr, x

def trim(x, sr, thresh=0.02):
    """Cut leading/trailing silence (keeps 40 ms either side)."""
    on = np.where(np.abs(x) > thresh)[0]
    if len(on) == 0: return x
    k = int(sr * 0.04)
    return x[max(0, on[0] - k): min(len(x), on[-1] + k)]

def radio(x, sr, rng, clip=False):
    x = trim(x, sr)
    pad = int(sr * (0.05 if clip else 0.6))
    x = np.concatenate([np.zeros(int(sr * (0.02 if clip else 0.25))), x, np.zeros(pad)])
    n = len(x); t = np.arange(n) / sr
    y = sosfilt(butter(4, [350, 2800], btype="band", fs=sr, output="sos"), x)
    y = np.tanh(y * 3.2) * 0.55                                    # cheap speaker, overdriven
    fade = 0.75 + 0.25 * np.sin(2 * np.pi * 0.31 * t + rng.uniform(0, 6)) * np.sin(2 * np.pi * 0.07 * t)
    y *= fade                                                      # signal strength wandering
    for _ in range(rng.integers(2, 5)):                            # dropouts
        a = rng.integers(0, n - sr // 4); b = a + rng.integers(sr // 40, sr // 8)
        y[a:b] *= 0.08
    hiss = sosfilt(butter(2, [800, 5000], btype="band", fs=sr, output="sos"), rng.normal(0, 1, n)) * 0.02
    crackle = np.zeros(n)
    idx = rng.integers(0, n, int(n / sr * 14))
    crackle[idx] = rng.uniform(-0.6, 0.6, len(idx))
    crackle = sosfilt(butter(2, 1500, btype="high", fs=sr, output="sos"), crackle)
    hum = 0.012 * np.sin(2 * np.pi * 50 * t) + 0.006 * np.sin(2 * np.pi * 150 * t)
    y = y + hiss + crackle + hum
    if not clip:
        sq = int(sr * 0.18)                                        # squelch tail
        y[-pad:-pad + sq] += rng.normal(0, 0.12, sq) * np.linspace(1, 0, sq)
    return y

def hull(y, sr):
    y = sosfilt(butter(3, 900, btype="low", fs=sr, output="sos"), y) * 1.6
    k = np.zeros(int(sr * 0.09)); k[0] = 1; k[int(sr * 0.031)] = 0.45; k[-1] = 0.25   # a little metal ring
    return np.convolve(y, k)[: len(y)]

def save(path, sr, y, peak=0.8):
    y = y / max(1e-6, np.abs(y).max()) * peak
    wavfile.write(path, sr, (y * 32767).astype(np.int16))

if __name__ == "__main__":
    src, out_radio, out_hull = sys.argv[1:4]
    rng = np.random.default_rng(int(sys.argv[4]) if len(sys.argv) > 4 else 1)
    sr, x = load(src)
    r = radio(x, sr, rng)
    save(out_radio, sr, r)
    save(out_hull, sr, hull(r, sr))
    print(f"{src} -> {out_radio}, {out_hull} ({len(r) / sr:.2f} s)")
