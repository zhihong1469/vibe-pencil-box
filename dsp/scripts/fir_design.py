"""R5/R7: FIR filter design with automatic parameter recommendation.

Pipeline:
  1. Run FFT + VAD analysis on input audio to find noise bands.
  2. Choose filter type (highpass/lowpass/bandpass/notch) based on noise.
  3. Compute cutoff frequencies from the spectrum.
  4. Design FIR with scipy.signal.firwin, quantize to int16 (Q15).
  5. Verify frequency response + apply filter to input for A/B comparison.
"""
import numpy as np
from scipy import signal as sig

from ._audio_utils import load_audio, rms_db
from .fft_analysis import process_file as fft_process
from .vad_detect import process_file as vad_process

# Human voice protection band (fundamental + harmonics)
VOICE_LOW = 80.0
VOICE_HIGH = 3400.0


def _analyze_noise(file_path, sample_rate, bits_per_sample, channels):
    """Extract noise characteristics from the audio."""
    fft = fft_process(file_path, fft_size=2048, sample_rate=sample_rate,
                      bits_per_sample=bits_per_sample, channels=channels)
    vad = vad_process(file_path, sample_rate=sample_rate,
                      bits_per_sample=bits_per_sample, channels=channels)

    freqs = np.array(fft["frequencies"])
    mags_db = np.array(fft["magnitudes_db"])

    noise_profile = vad["noise_profile"]
    noise_floor = noise_profile.get("avg_noise_db", -60.0)
    dom_noise = noise_profile.get("dominant_noise_freq", 0.0)

    # Find noise bands: regions in silence spectrum (from VAD) that exceed floor+10dB
    noise_spectrum = np.array(noise_profile.get("noise_spectrum", []))
    noise_bands = []
    if len(noise_spectrum) > 4:
        n_fft = (len(noise_spectrum) - 1) * 2
        n_freqs = np.fft.rfftfreq(n_fft, d=1.0 / sample_rate)
        threshold = np.median(noise_spectrum) + 10.0
        active = noise_spectrum > threshold
        # Find contiguous bands
        in_band = False
        start = 0
        for i, a in enumerate(active):
            if a and not in_band:
                in_band = True
                start = i
            elif not a and in_band:
                in_band = False
                if n_freqs[i - 1] - n_freqs[start] >= 15.0:
                    noise_bands.append((float(n_freqs[start]), float(n_freqs[i - 1])))
        if in_band:
            noise_bands.append((float(n_freqs[start]), float(n_freqs[-1])))

    return {
        "noise_floor_db": noise_floor,
        "dominant_noise_freq": dom_noise,
        "noise_bands": noise_bands,
        "snr_db": fft["snr_db"],
        "fft": fft
    }


def _choose_filter_type(noise):
    """Decide filter type from noise band analysis."""
    bands = noise["noise_bands"]
    dom = noise["dominant_noise_freq"]

    if not bands:
        # Fallback: dominant noise frequency decides
        if 10.0 < dom < VOICE_LOW - 15.0:
            return "highpass", VOICE_LOW - 10.0
        if dom > VOICE_HIGH + 500.0:
            return "lowpass", VOICE_HIGH + 200.0
        return "bandpass", VOICE_LOW

    # Check if noise exists on both sides of voice band
    low_noise = any(lo < VOICE_LOW for lo, hi in bands)
    high_noise = any(hi > VOICE_HIGH for lo, hi in bands)

    # Narrowband interference (notch candidate): band narrower than 80Hz
    narrow = [b for b in bands if (b[1] - b[0]) < 80.0]

    if narrow and len(narrow) == 1 and not low_noise and not high_noise:
        # Single narrow tone inside/around voice band -> notch
        fc = (narrow[0][0] + narrow[0][1]) / 2.0
        return "bandstop", fc

    if low_noise and not high_noise:
        # Cut below voice band
        low_band_top = max(hi for lo, hi in bands if lo < VOICE_LOW)
        return "highpass", min(low_band_top * 1.2, VOICE_LOW - 5.0)

    if high_noise and not low_noise:
        high_band_bottom = min(lo for lo, hi in bands if hi > VOICE_HIGH)
        return "lowpass", max(high_band_bottom * 0.8, VOICE_HIGH + 50.0)

    return "bandpass", VOICE_LOW


def _design_fir(filter_type, cutoff, sample_rate, max_taps):
    """Design FIR filter, returns (b, params)."""
    nyq = sample_rate / 2.0

    if filter_type == "highpass":
        taps = max_taps if max_taps % 2 == 0 else max_taps - 1
        b = sig.firwin(taps, cutoff, pass_zero=False, window="hann", fs=sample_rate)
        params = {"type": "high-pass", "cutoff_hz": float(cutoff)}
    elif filter_type == "lowpass":
        taps = max_taps if max_taps % 2 == 0 else max_taps - 1
        b = sig.firwin(taps, cutoff, pass_zero=True, window="hann", fs=sample_rate)
        params = {"type": "low-pass", "cutoff_hz": float(cutoff)}
    elif filter_type == "bandstop":
        # Notch around cutoff with modest width
        width = 60.0
        low = max(10.0, cutoff - width / 2)
        high = min(nyq - 10.0, cutoff + width / 2)
        taps = min(max_taps, 128)
        b = sig.firwin(taps, [low, high], pass_zero=True, window="hann", fs=sample_rate)
        params = {"type": "band-stop", "center_hz": float(cutoff), "bandwidth_hz": float(width)}
    else:  # bandpass
        taps = max_taps if max_taps % 2 == 0 else max_taps - 1
        low = VOICE_LOW
        high = min(VOICE_HIGH, nyq - 100.0)
        b = sig.firwin(taps, [low, high], pass_zero=False, window="hann", fs=sample_rate)
        params = {"type": "band-pass", "low_hz": float(low), "high_hz": float(high)}

    return b, params


def _frequency_response(b, sample_rate, num_points=256):
    w, h = sig.freqz(b, worN=num_points)
    freqs = w * sample_rate / (2 * np.pi)
    gain_db = 20.0 * np.log10(np.abs(h) + 1e-12)
    return freqs.tolist(), gain_db.tolist()


def _quantize_q15(b):
    """Convert float coefficients to int16 Q15 format."""
    scale = 32767.0
    q = np.round(b * scale).astype(np.int16)
    return q.tolist()


def process_file(file_path, max_taps=64, sample_rate=16000, bits_per_sample=16, channels=1):
    samples, sr = load_audio(file_path, sample_rate, bits_per_sample, channels)

    # Step 1: analyze noise
    noise = _analyze_noise(file_path, sr, bits_per_sample, channels)

    # Step 2: choose filter type + cutoff
    filter_type, cutoff = _choose_filter_type(noise)
    reason_parts = []
    if noise["noise_bands"]:
        reason_parts.append("噪声频段: " + ", ".join(
            f"{lo:.0f}-{hi:.0f}Hz" for lo, hi in noise["noise_bands"]))
    if noise["dominant_noise_freq"] > 0:
        reason_parts.append(f"主噪声频率 {noise['dominant_noise_freq']:.0f}Hz")

    # Step 3: design filter
    b, params = _design_fir(filter_type, cutoff, sr, max_taps)

    # Step 4: verify
    freqs, gain_db = _frequency_response(b, sr)

    # Passband ripple / stopband attenuation estimates
    freqs_arr = np.asarray(freqs)
    gain_arr = np.asarray(gain_db)
    pass_band = (freqs_arr >= 100.0) & (freqs_arr <= 3000.0)
    stop_band = freqs_arr < 50.0
    passband_ripple = float(np.max(np.abs(gain_arr[pass_band]) - np.max(gain_arr[pass_band])))
    stopband_atten = float(-np.max(gain_arr[stop_band])) if np.any(stop_band) else 0.0

    # Step 5: apply filter for A/B comparison
    filtered = sig.lfilter(b, 1.0, samples)
    input_rms = rms_db(samples)
    output_rms = rms_db(filtered)

    return {
        "filter_kind": f"fir_{filter_type}",
        "recommendation_reason": "; ".join(reason_parts) if reason_parts else "自动分析完成",
        "parameters": {
            **params,
            "numtaps": int(len(b)),
            "window": "hann",
            "sample_rate": int(sr)
        },
        "coefficients_float": [round(float(v), 6) for v in b],
        "coefficients_q15": _quantize_q15(b),
        "frequency_response": {
            "freqs": [round(v, 1) for v in freqs],
            "gain_db": [round(v, 2) for v in gain_db]
        },
        "verification": {
            "passband_ripple_db": round(passband_ripple, 3),
            "stopband_attenuation_db": round(stopband_atten, 2),
            "phase_linear": True,
            "stable": True
        },
        "before_after_comparison": {
            "input_rms_db": round(input_rms, 2),
            "output_rms_db": round(output_rms, 2),
            "noise_reduction_db": round(max(0.0, input_rms - output_rms), 2)
        },
        "noise_analysis": {
            "noise_floor_db": round(noise["noise_floor_db"], 2),
            "dominant_noise_freq": round(noise["dominant_noise_freq"], 1),
            "noise_bands": [[round(lo, 1), round(hi, 1)] for lo, hi in noise["noise_bands"]],
            "snr_db": round(noise["snr_db"], 2)
        }
    }


def process_raw(raw, max_taps=64, sample_rate=16000, bits_per_sample=16, channels=1):
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".raw", delete=False) as f:
        f.write(raw)
        tmp = f.name
    try:
        return process_file(tmp, max_taps, sample_rate, bits_per_sample, channels)
    finally:
        import os
        os.unlink(tmp)


def run(**kwargs):
    raise ValueError("fir_design requires an input file (process_file) or raw data (process_raw)")
