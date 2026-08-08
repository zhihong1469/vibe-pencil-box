"""Shared audio loading utilities for all DSP scripts."""
import numpy as np


def load_audio(file_path, sample_rate=16000, bits_per_sample=16, channels=1):
    """Load audio from a WAV file or raw PCM file.

    For .wav files, uses scipy.io.wavfile (16-bit PCM or float).
    For .raw/.pcm files, treats as raw signed PCM.

    Returns (samples_float64, sample_rate).
    samples_float64 is normalized to [-1.0, 1.0].
    """
    ext = file_path.rsplit('.', 1)[-1].lower()

    if ext == 'wav':
        from scipy.io import wavfile
        sr, data = wavfile.read(file_path)

        # Convert to float64 normalized
        if data.dtype == np.int16:
            samples = data.astype(np.float64) / 32768.0
        elif data.dtype == np.int32:
            samples = data.astype(np.float64) / 2147483648.0
        elif data.dtype == np.uint8:
            samples = (data.astype(np.float64) - 128.0) / 128.0
        elif data.dtype == np.float32 or data.dtype == np.float64:
            samples = data.astype(np.float64)
        else:
            samples = data.astype(np.float64)

        # If stereo, downmix to mono
        if len(samples.shape) > 1 and samples.shape[1] > 1:
            samples = samples.mean(axis=1)

        return samples, sr

    else:
        # Raw PCM
        with open(file_path, 'rb') as f:
            raw = f.read()

        if bits_per_sample == 16:
            samples = np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0
        elif bits_per_sample == 8:
            samples = (np.frombuffer(raw, dtype=np.uint8).astype(np.float64) - 128.0) / 128.0
        elif bits_per_sample == 32:
            samples = np.frombuffer(raw, dtype=np.int32).astype(np.float64) / 2147483648.0
        else:
            samples = np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0

        # Downmix if needed (interleaved)
        if channels > 1:
            n = len(samples) // channels
            samples = samples[:n * channels].reshape(-1, channels).mean(axis=1)

        return samples, sample_rate


def rms_db(samples):
    """Calculate RMS level in dBFS."""
    rms = np.sqrt(np.mean(samples ** 2))
    if rms < 1e-12:
        return -120.0
    return 20.0 * np.log10(rms)


def peak_db(samples):
    """Calculate peak level in dBFS."""
    peak = np.max(np.abs(samples))
    if peak < 1e-12:
        return -120.0
    return 20.0 * np.log10(peak)
