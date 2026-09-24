"""R1: PCM waveform statistics and visualization data.

Input: WAV/RAW audio file.
Output: waveform samples (downsampled for display) + level statistics.
Optional: base64 PNG waveform plot.
"""
import base64
import io

import numpy as np

from ._audio_utils import load_audio, rms_db, peak_db


def _render_waveform_image(samples, sample_rate, width=800, height=240, dpi=100):
    """Generate a waveform PNG as base64 string."""
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
    except ImportError:
        return None

    duration = len(samples) / sample_rate
    time_axis = np.linspace(0.0, duration, len(samples))

    fig, ax = plt.subplots(figsize=(width / dpi, height / dpi), dpi=dpi)
    ax.plot(time_axis, samples, color='#2196F3', linewidth=0.5)
    ax.set_xlim(0.0, duration)
    y_max = max(0.01, np.max(np.abs(samples)))
    ax.set_ylim(-y_max, y_max)
    ax.set_xlabel('Time (s)')
    ax.set_ylabel('Amplitude')
    ax.set_title('Waveform')
    ax.grid(True, alpha=0.3)
    fig.tight_layout()

    buf = io.BytesIO()
    fig.savefig(buf, format='png', dpi=dpi, facecolor='white', edgecolor='none')
    plt.close(fig)
    buf.seek(0)
    return base64.b64encode(buf.read()).decode('ascii')


def process_file(file_path, max_points=4000, sample_rate=16000, bits_per_sample=16,
                 channels=1, generate_image=False, **kwargs):
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

    result = {
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

    if generate_image:
        result["image_base64"] = _render_waveform_image(samples, sr)

    return result


def process_raw(raw, max_points=4000, sample_rate=16000, bits_per_sample=16,
                channels=1, generate_image=False, **kwargs):
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".raw", delete=False) as f:
        f.write(raw)
        tmp = f.name
    try:
        return process_file(tmp, max_points, sample_rate, bits_per_sample, channels, generate_image, **kwargs)
    finally:
        import os
        os.unlink(tmp)


def run(**kwargs):
    raise ValueError("waveform_render requires an input file (process_file) or raw data (process_raw)")
