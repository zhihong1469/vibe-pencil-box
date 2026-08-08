"""R4: Voice Activity Detection (VAD).

Energy-based VAD that marks voice/silence frames, extracts noise profile
from silent segments, and reports dominant noise frequency.

Algorithm:
  1. Split audio into frames (default 20ms).
  2. Compute per-frame RMS energy.
  3. Adaptive threshold = max(noise_floor, percentile-based).
  4. Smooth with hangover to avoid chatter.
"""
import numpy as np

from ._audio_utils import load_audio


def process_file(file_path, frame_ms=20, sample_rate=16000, bits_per_sample=16, channels=1,
                 hangover_frames=5, min_voice_ms=150):
    samples, sr = load_audio(file_path, sample_rate, bits_per_sample, channels)

    frame_len = int(sr * frame_ms / 1000)
    if frame_len < 1:
        raise ValueError("frame_ms too small for sample rate")

    # Split into frames
    n_frames = len(samples) // frame_len
    if n_frames < 2:
        raise ValueError("Audio too short for VAD")

    frames = samples[:n_frames * frame_len].reshape(n_frames, frame_len)
    energies = np.sqrt(np.mean(frames ** 2, axis=1))  # per-frame RMS
    energy_db = 20.0 * np.log10(energies + 1e-12)

    # "Absolute silence" frames (energy floored at -240dB) carry no noise info.
    # Estimate noise floor from remaining frames' lowest 30%; fall back to a
    # sane default if the audio is pure digital silence.
    abs_silence = energy_db <= -100.0
    analysis_frames = energy_db[~abs_silence]
    if len(analysis_frames) >= 3:
        sorted_idx = np.argsort(energy_db[~abs_silence])
        n_noise = max(1, int(len(analysis_frames) * 0.3))
        noise_floor_db = float(np.median(analysis_frames[sorted_idx[:n_noise]]))
    elif len(analysis_frames) > 0:
        noise_floor_db = float(np.min(analysis_frames))
    else:
        noise_floor_db = -120.0

    # Threshold: noise floor + dynamic offset
    # Use frames above the noise floor (excluding absolute silence) to gauge
    # the dynamic range of actual content.
    voice_energy = energy_db[(~abs_silence) & (energy_db > noise_floor_db)]
    if len(voice_energy) > 0:
        p90 = float(np.percentile(voice_energy, 90))
        offset = max(6.0, (p90 - noise_floor_db) * 0.4)
    else:
        offset = 10.0
    threshold_db = noise_floor_db + offset

    # Initial classification
    labels = energy_db > threshold_db
    labels = labels.astype(int)

    # Hangover smoothing: extend voice segments
    for _ in range(hangover_frames):
        shifted = np.zeros_like(labels)
        shifted[:-1] = labels[1:]
        labels = np.maximum(labels, shifted)

    # Find voice segments
    voice_segments = []
    in_voice = False
    start = 0
    for i, v in enumerate(labels):
        if v and not in_voice:
            in_voice = True
            start = i
        elif not v and in_voice:
            in_voice = False
            end = i - 1
            if (end - start + 1) * frame_ms >= min_voice_ms:
                voice_segments.append({
                    "start_ms": int(start * frame_ms),
                    "end_ms": int((end + 1) * frame_ms)
                })
    if in_voice and (n_frames - start) * frame_ms >= min_voice_ms:
        voice_segments.append({
            "start_ms": int(start * frame_ms),
            "end_ms": int(n_frames * frame_ms)
        })

    # Noise profile from silent frames (excluding absolute silence, which
    # carries no spectral information — e.g. digital zero padding)
    silence_mask = labels == 0
    candidate_frames = np.where(silence_mask & (~abs_silence))[0]
    if len(candidate_frames) == 0:
        candidate_frames = np.where(silence_mask)[0]
    if len(candidate_frames) > 0:
        # Aggregate noise spectrum using FFT average of silent frames
        fft_size = 512
        n_fft = min(fft_size, frame_len)
        spec_accum = np.zeros(n_fft // 2 + 1)
        count = 0
        for i in candidate_frames[:50]:
            frame = frames[i][:n_fft]
            frame = frame * np.hanning(len(frame))
            mag = np.abs(np.fft.rfft(frame))
            spec_accum += mag
            count += 1
        if count > 0:
            spec_accum /= count
            noise_spectrum = (20.0 * np.log10(spec_accum + 1e-12)).tolist()
            noise_freqs = np.fft.rfftfreq(n_fft, d=1.0 / sr)
            mask = noise_freqs > 10.0
            dom_noise_idx = np.argmax(spec_accum[mask])
            dominant_noise_freq = float(noise_freqs[mask][dom_noise_idx])
            avg_noise_db = float(np.mean(20.0 * np.log10(spec_accum + 1e-12)))
        else:
            noise_spectrum = []
            dominant_noise_freq = 0.0
            avg_noise_db = noise_floor_db
    else:
        noise_spectrum = []
        dominant_noise_freq = 0.0
        avg_noise_db = noise_floor_db

    # Frame labels
    frame_labels = [
        {
            "frame": i,
            "label": "voice" if v else "silence",
            "energy_db": round(float(energy_db[i]), 2)
        }
        for i, v in enumerate(labels)
    ]

    return {
        "frame_duration_ms": int(frame_ms),
        "total_frames": int(n_frames),
        "threshold_db": round(threshold_db, 2),
        "noise_floor_db": round(noise_floor_db, 2),
        "labels": frame_labels,
        "voice_segments": voice_segments,
        "voice_ratio": round(float(np.mean(labels)), 3),
        "noise_profile": {
            "avg_noise_db": round(avg_noise_db, 2),
            "noise_spectrum": [round(v, 2) for v in noise_spectrum],
            "dominant_noise_freq": round(dominant_noise_freq, 1)
        }
    }


def process_raw(raw, frame_ms=20, sample_rate=16000, bits_per_sample=16, channels=1):
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".raw", delete=False) as f:
        f.write(raw)
        tmp = f.name
    try:
        return process_file(tmp, frame_ms, sample_rate, bits_per_sample, channels)
    finally:
        import os
        os.unlink(tmp)


def run(**kwargs):
    raise ValueError("vad_detect requires an input file (process_file) or raw data (process_raw)")
