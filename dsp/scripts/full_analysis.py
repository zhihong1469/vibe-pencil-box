"""R1~R9: One-click full analysis pipeline.

Runs waveform → FFT → VAD → filter design → AGC → parameter package,
and returns a combined report. Each step's result is tagged so the
C# side can route it into the DspFullReport.
"""
import base64
import json

from . import waveform_render, fft_analysis, vad_detect, fir_design, agc_analyze


def _build_param_package(filter_result, agc_result):
    """Pack FIR coefficients + AGC params into a device download binary blob.

    Layout:
      Byte 0:    magic 'V' (0x56)
      Byte 1:    version 0x01
      Byte 2:    FIR filter type (0=HP,1=LP,2=BP,3=BS)
      Byte 3-4:  FIR taps (uint16 BE)
      Byte 5-6:  cutoff/center freq (uint16 BE)
      Byte 7+:   FIR Q15 coefficients (int16 BE × taps)
      ... AGC section
    """
    try:
        params = filter_result.get("parameters", {})
        type_map = {"high-pass": 0, "low-pass": 1, "band-pass": 2, "band-stop": 3}
        ftype = type_map.get(params.get("type", ""), 0)
        taps = int(params.get("numtaps", 0))
        cutoff = int(params.get("cutoff_hz",
                      params.get("center_hz",
                      params.get("low_hz", 0))))
        coefs = filter_result.get("coefficients_q15", [])[:taps]

        agc = agc_result.get("recommended_params", {})

        buf = bytearray()
        buf.append(0x56)  # magic
        buf.append(0x01)  # version
        buf.append(ftype)
        buf += bytes([(taps >> 8) & 0xFF, taps & 0xFF])
        buf += bytes([(cutoff >> 8) & 0xFF, cutoff & 0xFF])

        for c in coefs:
            v = int(c) & 0xFFFF
            buf += bytes([(v >> 8) & 0xFF, v & 0xFF])

        # AGC params: 7 × int16 BE (values in 0.1 dB units where applicable)
        agc_vals = [
            int(agc.get("target_level_dbfs", -20) * 10),
            int(agc.get("threshold_dbfs", -10) * 10),
            int(agc.get("max_gain_db", 10) * 10),
            int(agc.get("compression_ratio", 2) * 10),
            int(agc.get("attack_time_ms", 5)),
            int(agc.get("release_time_ms", 200)),
            int(agc.get("noise_gate_dbfs", -50) * 10),
        ]
        for v in agc_vals:
            v = v & 0xFFFF
            buf += bytes([(v >> 8) & 0xFF, v & 0xFF])

        return base64.b64encode(bytes(buf)).decode("ascii")
    except Exception:
        return None


def process_file(file_path, max_taps=64, sample_rate=16000,
                 bits_per_sample=16, channels=1, generate_plots=False):
    steps = {}
    plots = {}

    steps["waveform"] = waveform_render.process_file(
        file_path, sample_rate=sample_rate,
        bits_per_sample=bits_per_sample, channels=channels,
        generate_image=generate_plots)

    if generate_plots and "image_base64" in steps["waveform"]:
        plots["waveform"] = steps["waveform"].pop("image_base64")

    steps["fft"] = fft_analysis.process_file(
        file_path, sample_rate=sample_rate,
        bits_per_sample=bits_per_sample, channels=channels,
        generate_image=generate_plots)

    if generate_plots and "image_base64" in steps["fft"]:
        plots["spectrum"] = steps["fft"].pop("image_base64")

    steps["vad"] = vad_detect.process_file(
        file_path, sample_rate=sample_rate,
        bits_per_sample=bits_per_sample, channels=channels)

    steps["filter"] = fir_design.process_file(
        file_path, max_taps=max_taps, sample_rate=sample_rate,
        bits_per_sample=bits_per_sample, channels=channels)

    steps["agc"] = agc_analyze.process_file(
        file_path, sample_rate=sample_rate,
        bits_per_sample=bits_per_sample, channels=channels)

    # Parameter package
    steps["param_package"] = _build_param_package(steps["filter"], steps["agc"])

    # Summary
    fft = steps["fft"]
    filt = steps["filter"]
    agc = steps["agc"]
    summary = (
        f"FFT: 主频 {fft['dominant_freq']:.0f}Hz, SNR {fft['snr_db']:.1f}dB | "
        f"滤波: {filt['parameters']['type']} {filt['parameters'].get('cutoff_hz', filt['parameters'].get('center_hz', ''))}Hz "
        f"({filt['parameters']['numtaps']}阶) | "
        f"AGC: 目标 {agc['recommended_params']['target_level_dbfs']:.0f}dBFS, "
        f"阈值 {agc['recommended_params']['threshold_dbfs']:.0f}dBFS"
    )

    return {"steps": steps, "summary": summary, "plots": plots}


def process_raw(raw, max_taps=64, sample_rate=16000, bits_per_sample=16, channels=1, generate_plots=False):
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".raw", delete=False) as f:
        f.write(raw)
        tmp = f.name
    try:
        return process_file(tmp, max_taps, sample_rate, bits_per_sample, channels, generate_plots)
    finally:
        import os
        os.unlink(tmp)


def run(**kwargs):
    raise ValueError("full_analysis requires an input file (process_file) or raw data (process_raw)")
