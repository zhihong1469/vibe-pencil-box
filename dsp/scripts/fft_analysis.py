"""R2: FFT spectrum analysis.

Input: WAV/RAW audio file.
Output: frequency spectrum (dB), peaks, SNR estimate, dominant frequency.
Optional: base64 PNG spectrum plot.
"""
import base64
import io

import numpy as np

from ._audio_utils import load_audio, rms_db

WINDOWS = {
    "hann": np.hanning,
    "hamming": np.hamming,
    "blackman": np.blackman,
    "rect": np.ones,
}


def _render_spectrum_image(freqs, mags_db, peaks, width=800, height=240, dpi=100):
    """Generate a spectrum PNG as base64 string."""
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
    except ImportError:
        return None

    fig, ax = plt.subplots(figsize=(width / dpi, height / dpi), dpi=dpi)
    ax.plot(freqs, mags_db, color='#4CAF50', linewidth=0.7)

    # Mark top peaks
    for pk in peaks[:3]:
        ax.axvline(pk["freq"], color='#FF5722', linestyle='--', linewidth=0.8, alpha=0.7)
        ax.text(pk["freq"], max(mags_db) - 3, f'{pk["freq"]:.0f}Hz', rotation=90, va='top', ha='right', color='#FF5722', fontsize=7)

    ax.set_xlim(0.0, min(8000.0, max(freqs)))
    ax.set_ylim(min(mags_db) - 5, max(mags_db) + 3)
    ax.set_xlabel('Frequency (Hz)')
    ax.set_ylabel('Magnitude (dB)')
    ax.set_title('FFT Spectrum')
    ax.grid(True, alpha=0.3)
    fig.tight_layout()

    buf = io.BytesIO()
    fig.savefig(buf, format='png', dpi=dpi, facecolor='white', edgecolor='none')
    plt.close(fig)
    buf.seek(0)
    return base64.b64encode(buf.read()).decode('ascii')


def process_file(file_path, fft_size=1024, window="hann", sample_rate=16000,
                 bits_per_sample=16, channels=1, num_peaks=5, generate_image=False, **kwargs):
    samples, sr = load_audio(file_path, sample_rate, bits_per_sample, channels)

    if window not in WINDOWS:
        raise ValueError(f"Unknown window: {window}, choose from {list(WINDOWS.keys())}")

    n = min(len(samples), fft_size)
    if n < 2:
        raise ValueError("Audio too short for FFT")

    win = WINDOWS[window](n)
    frame = samples[:n] * win

    # Normalize by window energy for accurate magnitude
    scale = np.sum(win) if np.sum(win) > 0 else 1.0
    fft_result = np.fft.rfft(frame) / scale * 2.0
    mags = np.abs(fft_result)
    mags_db = 20.0 * np.log10(mags + 1e-12)
    freqs = np.fft.rfftfreq(n, d=1.0 / sr)

    # Peak detection (skip DC, use local maxima)
    peaks = []
    search = mags_db[1:]
    idx = np.argpartition(search, -num_peaks * 2)[-num_peaks * 2:]
    idx = idx[np.argsort(search[idx])[::-1]]
    for i in idx:
        freq = float(freqs[i + 1])
        mag = float(search[i])
        if freq > 10.0:  # ignore below 10Hz (DC noise)
            peaks.append({"freq": round(freq, 1), "magnitude_db": round(mag, 1)})
        if len(peaks) >= num_peaks:
            break

    # Dominant frequency (highest peak above 10Hz)
    mask = freqs > 10.0
    dom_idx = np.argmax(mags[mask])
    dominant_freq = float(freqs[mask][dom_idx])

    # Noise floor estimation: median of magnitudes in high-frequency band (>3400Hz)
    nf_mask = freqs > 3400.0
    if np.any(nf_mask):
        noise_floor_db = float(np.median(mags_db[nf_mask]))
    else:
        noise_floor_db = float(np.median(mags_db[-20:]))

    # SNR estimate: voice band (80-3400Hz) avg vs out-of-band avg
    voice_mask = (freqs >= 80.0) & (freqs <= 3400.0)
    out_mask = freqs > 3400.0
    if np.any(voice_mask) and np.any(out_mask):
        voice_power = np.mean(mags[voice_mask] ** 2)
        noise_power = np.mean(mags[out_mask] ** 2)
        # 近静音段 voice_power 可能下溢为 0 → log10 除零 → -Inf，桥端 JSON 解析会崩
        if voice_power <= 0.0 or not np.isfinite(voice_power):
            snr_db = -180.0
        else:
            snr_db = float(10.0 * np.log10(voice_power / (noise_power + 1e-12)))
    else:
        snr_db = 0.0

    result = {
        "sample_rate": int(sr),
        "fft_size": int(n),
        "window_function": window,
        "frequencies": [round(f, 3) for f in freqs],
        "magnitudes_db": [round(m, 2) for m in mags_db],
        "peaks": peaks,
        "snr_db": round(snr_db, 2),
        "noise_floor_db": round(noise_floor_db, 2),
        "dominant_freq": round(dominant_freq, 1),
        "rms_db": round(rms_db(samples), 2)
    }

    if generate_image:
        result["image_base64"] = _render_spectrum_image(freqs, mags_db, peaks)

    return result


def process_raw(raw, fft_size=1024, window="hann", sample_rate=16000,
                bits_per_sample=16, channels=1, num_peaks=5, generate_image=False, **kwargs):
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".raw", delete=False) as f:
        f.write(raw)
        tmp = f.name
    try:
        return process_file(tmp, fft_size, window, sample_rate, bits_per_sample, channels, num_peaks, generate_image, **kwargs)
    finally:
        import os
        os.unlink(tmp)


def run(**kwargs):
    raise ValueError("fft_analysis requires an input file (process_file) or raw data (process_raw)")
