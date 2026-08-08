"""R8: AGC parameter analysis and recommendation.

Analyzes PCM level statistics (separated by VAD voice/silence), then
recommends AGC parameters (target level, threshold, max gain, compression,
attack/release, noise gate) and simulates the AGC to verify.
"""
import numpy as np

from ._audio_utils import load_audio, rms_db
from .vad_detect import process_file as vad_process


def _frame_rms_db(samples, sr, frame_ms=10):
    frame_len = max(1, int(sr * frame_ms / 1000))
    n = len(samples) // frame_len
    if n == 0:
        return np.array([])
    frames = samples[:n * frame_len].reshape(n, frame_len)
    rms = np.sqrt(np.mean(frames ** 2, axis=1))
    return 20.0 * np.log10(rms + 1e-12)


def _simulate_agc(samples, sr, target_db, threshold_db, max_gain_db, ratio,
                  noise_gate_db, attack_ms, release_ms):
    """Simple feed-forward AGC simulation. Returns processed samples + stats."""
    target_lin = 10 ** (target_db / 20.0)
    threshold_lin = 10 ** (threshold_db / 20.0)
    max_gain_lin = 10 ** (max_gain_db / 20.0)
    noise_gate_lin = 10 ** (noise_gate_db / 20.0)

    frame_len = max(1, int(sr * 0.005))  # 5ms control frames
    n = len(samples) // frame_len
    if n == 0:
        return samples.copy(), {"gain_reduction_events": 0}

    frames = samples[:n * frame_len].reshape(n, frame_len)
    gain = 1.0
    attack_coef = np.exp(-1.0 / (attack_ms / 1000.0 * sr / frame_len)) if attack_ms > 0 else 0.0
    release_coef = np.exp(-1.0 / (release_ms / 1000.0 * sr / frame_len)) if release_ms > 0 else 0.0

    output = np.zeros_like(samples)
    reduction_events = 0

    for i, frame in enumerate(frames):
        level = float(np.max(np.abs(frame)))
        rms = float(np.sqrt(np.mean(frame ** 2)))

        # Noise gate: below gate -> mute-ish (keep small gain)
        if rms < noise_gate_lin:
            target_gain = 1.0
        else:
            # Above threshold -> compress
            if level > threshold_lin:
                over_db = 20.0 * np.log10(level / threshold_lin)
                compressed_db = over_db / ratio
                target_gain = min(max_gain_lin, (threshold_lin / (level + 1e-12)) * 10 ** (compressed_db / 20.0))
                if target_gain < gain - 1e-3:
                    reduction_events += 1
            else:
                # Boost toward target level
                target_gain = min(max_gain_lin, target_lin / (rms + 1e-12))

        # Smooth gain changes
        if target_gain < gain:
            gain = gain * (1 - attack_coef) + target_gain * attack_coef
        else:
            gain = gain * (1 - release_coef) + target_gain * release_coef

        gain = min(max_gain_lin, max(0.05, gain))
        output[i * frame_len:(i + 1) * frame_len] = frame * gain

    # Handle tail
    output[n * frame_len:] = samples[n * frame_len:] * gain

    return output, {"gain_reduction_events": reduction_events}


def process_file(file_path, sample_rate=16000, bits_per_sample=16, channels=1):
    samples, sr = load_audio(file_path, sample_rate, bits_per_sample, channels)

    vad = vad_process(file_path, sample_rate=sr, bits_per_sample=bits_per_sample, channels=channels)

    frame_db = _frame_rms_db(samples, sr)
    labels = vad["labels"]
    n_vad = len(labels)
    n_frames = len(frame_db)
    m = min(n_vad, n_frames)

    voice_mask = np.array([1 if labels[i]["label"] == "voice" else 0 for i in range(m)])
    silence_mask = 1 - voice_mask

    # Level statistics
    voice_rms = frame_db[:m][voice_mask == 1]
    silence_rms = frame_db[:m][silence_mask == 1]

    if len(voice_rms) == 0:
        # Fallback: use all frames, assume no VAD labels
        voice_rms = frame_db

    p10 = float(np.percentile(voice_rms, 10))
    p50 = float(np.percentile(voice_rms, 50))
    p90 = float(np.percentile(voice_rms, 90))
    p95 = float(np.percentile(voice_rms, 95))
    peak = float(np.max(np.abs(samples))) if len(samples) > 0 else 0.0
    peak_db = 20.0 * np.log10(peak + 1e-12)
    dynamic_range = float(p90 - p10)

    # Noise average: exclude absolute silence (digital zero) frames
    usable_silence = silence_rms[silence_rms > -100.0]
    if len(usable_silence) > 0:
        noise_avg = float(np.mean(usable_silence))
    else:
        noise_avg = p10 - 20.0

    # Clipping ratio
    clipped = np.sum(np.abs(samples) >= 0.999)
    clipping_ratio = float(clipped / len(samples)) if len(samples) > 0 else 0.0

    # Recommended parameters
    target_level = max(-30.0, min(-6.0, p50))
    threshold = min(0.0, p95 + 3.0)
    max_gain = min(30.0, max(1.0, target_level - p10))
    ratio = 4.0 if dynamic_range > 20.0 else 2.0
    noise_gate = noise_avg + 6.0

    # Simulate
    processed, sim_stats = _simulate_agc(
        samples, sr, target_level, threshold, max_gain, ratio, noise_gate, 5, 200)

    out_peak = float(np.max(np.abs(processed)))
    out_peak_db = 20.0 * np.log10(out_peak + 1e-12)
    out_clip = np.sum(np.abs(processed) >= 0.999)
    out_clip_ratio = float(out_clip / len(processed)) if len(processed) > 0 else 0.0

    return {
        "level_statistics": {
            "voice_rms_p10_dbfs": round(p10, 2),
            "voice_rms_p50_dbfs": round(p50, 2),
            "voice_rms_p90_dbfs": round(p90, 2),
            "voice_peak_dbfs": round(peak_db, 2),
            "noise_rms_avg_dbfs": round(noise_avg, 2),
            "dynamic_range_db": round(dynamic_range, 2),
            "clipping_ratio": round(clipping_ratio, 6)
        },
        "recommended_params": {
            "target_level_dbfs": round(target_level, 2),
            "threshold_dbfs": round(threshold, 2),
            "max_gain_db": round(max_gain, 2),
            "compression_ratio": ratio,
            "attack_time_ms": 5,
            "release_time_ms": 200,
            "noise_gate_dbfs": round(noise_gate, 2)
        },
        "simulation_result": {
            "output_rms_p50_dbfs": round(float(np.percentile(
                _frame_rms_db(processed, sr), 50)), 2),
            "output_peak_dbfs": round(out_peak_db, 2),
            "output_clipping_ratio": round(out_clip_ratio, 6),
            "gain_reduction_events": sim_stats["gain_reduction_events"]
        }
    }


def process_raw(raw, sample_rate=16000, bits_per_sample=16, channels=1):
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".raw", delete=False) as f:
        f.write(raw)
        tmp = f.name
    try:
        return process_file(tmp, sample_rate, bits_per_sample, channels)
    finally:
        import os
        os.unlink(tmp)


def run(**kwargs):
    raise ValueError("agc_analyze requires an input file (process_file) or raw data (process_raw)")
