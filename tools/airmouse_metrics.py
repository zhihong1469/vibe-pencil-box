#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""空鼠遥测录波指标脚本（pc-issues 对齐卷 06 §4；纯离线，输入=record CSV）。

用法:
    python tools/airmouse_metrics.py <record.csv> [--json]

五指标（对齐卷 06 §4 表）:
    静止漂移 RMS  静止段（非冻结且 out=0）rate 去均值 RMS —— 手停光标稳
    移动抖动      移动段 rate 逐帧高频差分 RMS —— 小动不抖
    过冲          快速甩停后 300ms 窗内 out 反向过零面积 —— 大动收得住
    延迟          raw|rate| 与 |out| 互相关峰值偏移（帧粒度）—— 跟手感
    丢流          seq 缺口计数/率（与 # meta 尾注互校）—— 链路质量（应 <0.1%）

口径:
    - CSV 全部**定点直出**（对齐卷 06 §1 笔端落码定案，原"LSB 原码"提案作废）：
      raw 陀螺=0.01dps、raw 加速度=0.001g、rate=0.01dps、out=HID 原值——全物理量，PC 不换算。
    - 极端甩笔 >±327.67dps 帧饱和（|raw|=32767，非回绕）；报告附饱和帧计数，指标可选剔除（v1 保留并计数）。
    - 静止/移动分段为 v1 启发式（out 是否为 0 + 是否冻结位），阈值后续可用真录波校准（S17）。
"""

import argparse
import csv
import json
import math
import sys

COLS = ["pc_rx_ts", "seq", "flags", "raw_gx", "raw_gy", "raw_gz",
        "raw_ax", "raw_ay", "raw_az", "rate_yaw", "rate_roll", "out_x", "out_y"]

FLAG_FROZEN = 0x01


def load(path):
    rows, meta = [], {}
    with open(path, newline="", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            if line.startswith("# meta:"):
                for kv in line[len("# meta:"):].split(","):
                    if "=" in kv:
                        k, v = kv.split("=", 1)
                        meta[k.strip()] = v.strip()
                continue
            if line.startswith("#") or line.startswith("pc_rx_ts"):
                continue
            parts = line.split(",")
            if len(parts) != len(COLS):
                continue
            rows.append({k: int(v) for k, v in zip(COLS, parts)})
    return rows, meta


def mean(xs):
    return sum(xs) / len(xs) if xs else 0.0


def rms(xs):
    return math.sqrt(mean([x * x for x in xs])) if xs else 0.0


def split_segments(rows):
    """静止段=非冻结且 out 双 0；移动段=其余。返回 (静止行表, 移动行表)。"""
    static = [r for r in rows
              if not (r["flags"] & FLAG_FROZEN) and r["out_x"] == 0 and r["out_y"] == 0]
    moving = [r for r in rows
              if (r["flags"] & FLAG_FROZEN) or r["out_x"] != 0 or r["out_y"] != 0]
    return static, moving


def drift_rms(rows):
    """静止漂移：rate 去均值 RMS（双轴合并），0.01dps 定点 → dps。"""
    static, _ = split_segments(rows)
    vals = [r["rate_yaw"] for r in static] + [r["rate_roll"] for r in static]
    if not vals:
        return None
    m = mean(vals)
    return rms([v - m for v in vals]) * 0.01, len(static), m * 0.01


def saturated_frames(rows):
    """饱和帧数：任一陀螺/加速度 raw == ±32767（定点满幅，极端甩笔削顶——非回绕）。"""
    axes = ("raw_gx", "raw_gy", "raw_gz", "raw_ax", "raw_ay", "raw_az")
    return sum(1 for r in rows if any(abs(r[a]) >= 32767 for a in axes))


def jitter_rms(rows):
    """移动抖动：移动段 rate 逐帧差分 RMS（双轴合并，0.01dps 定点 → dps）。"""
    _, moving = split_segments(rows)
    if len(moving) < 3:
        return None
    diffs = []
    for axis in ("rate_yaw", "rate_roll"):
        diffs += [moving[i + 1][axis] - moving[i][axis] for i in range(len(moving) - 1)]
    return rms(diffs) * 0.01, len(moving)


def overshoot(rows, window_ms=300):
    """过冲：快速甩停（|rate|>快阈值→落回低阈值）后窗内 out 反向面积（单位=HID 计数·帧）。"""
    FAST, STOP = 20000, 2000   # 0.01dps：200dps / 20dps（v1 阈值，真录波后校准）
    area, events = 0, 0
    for i in range(1, len(rows) - 1):
        prev, cur = rows[i - 1], rows[i]
        rate_prev = max(abs(prev["rate_yaw"]), abs(prev["rate_roll"]))
        rate_cur = max(abs(cur["rate_yaw"]), abs(cur["rate_roll"]))
        if rate_prev < FAST or rate_cur > STOP:
            continue
        # 甩停事件：停后 300ms 窗内 |out| 累计（反向过零面积的 v1 简化——只量"停后还动了多少"）
        j, end_ms = i + 1, cur["pc_rx_ts"] + window_ms
        while j < len(rows) and rows[j]["pc_rx_ts"] <= end_ms:
            area += abs(rows[j]["out_x"]) + abs(rows[j]["out_y"])
            j += 1
        events += 1
    return (area, events) if events else None


def latency(rows):
    """raw→out 互相关峰值偏移（帧粒度）：x=|rate_yaw|，y=|out_x|+|out_y|。"""
    if len(rows) < 32:
        return None
    x = [abs(r["rate_yaw"]) for r in rows]
    y = [abs(r["out_x"]) + abs(r["out_y"]) for r in rows]
    x = [v - mean(x) for v in x]
    y = [v - mean(y) for v in y]
    max_lag = min(40, len(rows) // 4)
    best_lag, best_corr = 0, -1e18
    for lag in range(0, max_lag):
        c = sum(x[i] * y[i + lag] for i in range(len(rows) - lag))
        if c > best_corr:
            best_corr, best_lag = c, lag
    span = rows[-1]["pc_rx_ts"] - rows[0]["pc_rx_ts"]
    ms_per_frame = span / max(1, len(rows) - 1)
    return best_lag, best_lag * ms_per_frame


def seq_dropped(rows):
    """重算 seq 缺口（u16 回绕），与 meta.dropped 互校。"""
    dropped = 0
    for i in range(1, len(rows)):
        delta = (rows[i]["seq"] - rows[i - 1]["seq"]) & 0xFFFF
        if 1 < delta <= 0x8000:
            dropped += delta - 1
    return dropped


def main():
    ap = argparse.ArgumentParser(description="空鼠录波指标（对齐卷 06 §4）")
    ap.add_argument("csv", help="record 产物 CSV")
    ap.add_argument("--json", action="store_true", help="输出 JSON（脚本取数）")
    args = ap.parse_args()

    rows, meta = load(args.csv)
    if not rows:
        print(f"无样本：{args.csv}（先 company record 产出 CSV）", file=sys.stderr)
        return 1

    span_ms = rows[-1]["pc_rx_ts"] - rows[0]["pc_rx_ts"]
    fps = meta.get("fps") or (f"{(len(rows) - 1) * 1000.0 / span_ms:.1f}" if span_ms > 0 else "0")
    dropped_meta = int(meta["dropped"]) if "dropped" in meta else None
    dropped_calc = seq_dropped(rows)

    drift = drift_rms(rows)
    jitter = jitter_rms(rows)
    over = overshoot(rows)
    lat = latency(rows)

    report = {
        "file": args.csv,
        "frames": len(rows),
        "fps": float(fps) if fps else 0.0,
        "span_ms": span_ms,
        "dropped_meta": dropped_meta,
        "dropped_calc": dropped_calc,
        "dropped_ratio": round(dropped_calc / max(1, len(rows) + dropped_calc), 6),
        "saturated_frames": saturated_frames(rows),
        "div": int(meta["div"]) if "div" in meta else None,
        "fw": meta.get("fw"),
    }
    if drift:
        report["drift_rms_dps"] = round(drift[0], 4)
        report["drift_static_frames"] = drift[1]
        report["drift_mean_dps"] = round(drift[2], 4)
    if jitter:
        report["jitter_rms_dps"] = round(jitter[0], 4)
        report["jitter_moving_frames"] = jitter[1]
    if over:
        report["overshoot_area"] = over[0]
        report["overshoot_events"] = over[1]
    if lat:
        report["latency_frames"] = lat[0]
        report["latency_ms_est"] = round(lat[1], 1)

    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print(f"== 空鼠录波指标：{args.csv}")
        print(f"   帧数 {len(rows)} @ {fps}fps（div={report['div']}，fw={report['fw']}）")
        print(f"   丢流  {dropped_calc}（重算）vs {dropped_meta}（meta）→ 比率 {report['dropped_ratio'] * 100:.3f}%（判据 <0.1%）")
        if report["saturated_frames"]:
            print(f"   饱和帧 {report['saturated_frames']}（|raw|=32767 削顶，极端甩笔——已知特性非故障）")
        if drift:
            print(f"   静止漂移 RMS = {drift[0]:.4f} dps（静止段 {drift[1]} 帧；去均值后）")
        if jitter:
            print(f"   移动抖动 RMS = {jitter[0]:.4f} dps（移动段 {jitter[1]} 帧；逐帧差分）")
        if over:
            print(f"   过冲面积    = {over[0]}（{over[1]} 次甩停事件；300ms 窗反向 |out| 累计）")
        if lat:
            print(f"   延迟        = {lat[0]} 帧（≈{lat[1]:.0f}ms @ {fps}fps；互相关峰值，帧粒度下限）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
