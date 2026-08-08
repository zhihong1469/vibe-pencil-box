"""Generate a test WAV: voice-band tones + 50Hz hum + high-freq hiss + silence gaps."""
import numpy as np
from scipy.io import wavfile

SR = 16000
DURATION = 4.0
N = int(SR * DURATION)
t = np.arange(N) / SR

# Voice-like signal: formants at 150/500/1200/2500 Hz, modulated amplitude
voice = (
    0.4 * np.sin(2 * np.pi * 150 * t) +
    0.25 * np.sin(2 * np.pi * 500 * t) +
    0.15 * np.sin(2 * np.pi * 1200 * t) +
    0.08 * np.sin(2 * np.pi * 2500 * t)
)
# Amplitude envelope simulating speech syllables
env = 0.5 + 0.5 * np.sin(2 * np.pi * 1.2 * t) ** 4
voice *= env

# 50Hz mains hum (low freq noise) — realistic level ~ -25 dBFS
hum = 0.08 * np.sin(2 * np.pi * 50 * t)

# High-frequency hiss (thermal noise > 3.4kHz)
rng = np.random.default_rng(42)
hiss = rng.normal(0, 0.015, N)
# band-pass hiss to 4-7kHz
from scipy.signal import butter, sosfiltfilt
sos = butter(6, [4000, 7000], btype='band', fs=SR, output='sos')
hiss = sosfiltfilt(sos, hiss) * 0.5

# Mix
signal = voice + hum + hiss

# Add silence gaps (simulating VAD silence).
# Real ambient noise (hum + hiss) persists during "silence"; only the
# voice component is gated, so VAD/noise extraction sees realistic noise.
noise_floor_only = hum + hiss
final = signal.copy()
final[int(0.2 * SR):int(0.45 * SR)] = noise_floor_only[int(0.2 * SR):int(0.45 * SR)]
final[int(1.6 * SR):int(1.9 * SR)] = noise_floor_only[int(1.6 * SR):int(1.9 * SR)]
final[int(3.0 * SR):int(3.4 * SR)] = noise_floor_only[int(3.0 * SR):int(3.4 * SR)]

# Normalize to -6 dBFS peak
peak = np.max(np.abs(final))
final = final / peak * 0.5

# Quantize to int16
pcm = (final * 32767).astype(np.int16)
wavfile.write("test_audio.wav", SR, pcm)
print(f"Wrote test_audio.wav: {DURATION}s, {SR}Hz, 16bit mono")
print(f"Peak: {np.max(np.abs(final)):.3f}, contains 50Hz hum + 4-7kHz hiss + silence gaps")
