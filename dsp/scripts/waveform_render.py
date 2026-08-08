"""R1: PCM waveform statistics and visualization data.

Input: WAV/RAW audio file.
Output: waveform samples (downsampled for display) + level statistics.
"""
import numpy as np

from ._audio_utils import load_audio, rms_db, peak_db


def process_file(file_path, max_points=4000, sample_rate=16000, bits_per_sample=16, channels=1):
    samples, sr = load_audio(file_path, sample_rate, bits_per_sample, channels)

    # Downsample for display (keep peak envelope per bucket)
    n = len(samples)
    step = max(1, n // max_points)
    display_samples = []
    for i in range(0, n, step):
        chunk = samples[i:i + step]
        if len(chunk) == 0:
            continue
        display_samples.append(float(np.max(np.abs(chunk)) * np.sign(chunk[np.argmax(np.abs(chunk))])))

    # Statistics
    rms = rms_db(samples)
    peak = peak_db(samples)
    dc_offset = float(np.mean(samples))

    # Clipping detection (|sample| >= 0.999)
    clipped = np.sum(np.abs(samples) >= 0.999)
    clipping_ratio = float(clipped / n) if n > 0 else 0.0

    return {
        "sample_rate": int(sr),
        "channels": 1,
        "duration_seconds": float(n / sr),
        "num_samples": int(n),
        "samples": display_samples,
        "stats": {
            "rms_db": round(rms, 2),
            "peak_db": round(peak, 2),
            "dc_offset": round(dc_offset, 5),
            "clipping_detected": bool(clipped > 0),
            "clipping_ratio": round(clipping_ratio, 6),
            "clipping_samples": int(clipped)
        }
    }


def process_raw(raw, sample_rate=16000, bits_per_sample=16, channels=1):
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".raw", delete=False) as f:
        f.write(raw)
        tmp = f.name
    try:
        return process_file(tmp, sample_rate=sample_rate, bits_per_sample=bits_per_sample, channels=channels)
    finally:
        import os
        os.unlink(tmp)


def run(**kwargs):
    raise ValueError("waveform_render requires an input file (process_file) or raw data (process_raw)")
