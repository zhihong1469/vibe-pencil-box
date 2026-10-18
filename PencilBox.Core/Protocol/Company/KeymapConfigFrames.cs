// <copyright file="KeymapConfigFrames.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Models;

namespace PencilBox.Core.Protocol.Company;

/// <summary>
/// `0x55` 快捷键可配置帧面（pc-issues/05 §2 草案的 PC 侧实现）。
///
/// 请求 `F5 AA AA [LEN] 61 01 55 [PARAM] ...`，应答 `F5 AA AA [LEN] E1 01 55 [PARAM] [STATUS] ...`
/// （请求方收到 CommandRequester 返回的 DATA 自带 `[PARAM]` 前缀，见各 TryParse）。
/// LEN 口径同 0x53：LEN = 3 + 载荷长。
///
/// ⚠️ 三端未裁决（§8 开放问题在案）、基站未实现——现网发送必然无应答，调用方按
/// "探测 → 无应答降级"消费（`CompanyKeymapConfigService`）；固件落地后本帧面即生效。
/// </summary>
public static class KeymapConfigFrames
{
    /// <summary>PARAM 01：读条目（slot=0xFF 读元信息）</summary>
    public const byte ParamReadEntry = 0x01;

    /// <summary>PARAM 02：写条目（RAM 运行表，不动 NVDS；可连发批量后一次 03）</summary>
    public const byte ParamWriteEntry = 0x02;

    /// <summary>PARAM 03：提交（RAM → NVDS 掉电保持，两段式 commit）</summary>
    public const byte ParamCommit = 0x03;

    /// <summary>PARAM 06：写切换表单行（RAM；mode=0 为基站内部行，固件应拒写，PC 不发）</summary>
    public const byte ParamWriteSwitch = 0x06;

    /// <summary>元信息探测：读条目 PARAM + slot=0xFF。</summary>
    public static byte[] EncodeMetaProbe() =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.KeymapConfig, [ParamReadEntry, 0xFF]);

    /// <summary>
    /// 写按键条目：`[02][slot][entry8]`。slot=固件物理槽位（s_keymap[] 下标）——
    /// PC 配置文档按**存储序=固件原序**保存（KeymapSorting.Canonical 只是显示序），故 slot=存储下标。
    /// </summary>
    public static byte[] EncodeWriteEntry(byte slot, KeymapEntry entry)
    {
        var payload = new byte[10];
        payload[0] = ParamWriteEntry;
        payload[1] = slot;
        entry.ToBytes().CopyTo(payload, 2);
        return CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.KeymapConfig, payload);
    }

    /// <summary>写切换表单行：`[06][mode][modifier][code]`（mode∈{1,2}；0=内部行拒发）。</summary>
    public static byte[] EncodeWriteSwitch(KeymapSwitchEntry entry)
    {
        if (KeymapRules.IsSwitchInternalRow(entry.Mode))
            throw new ArgumentException($"mode=0 为基站内部行（{KeymapRules.SwitchIdleLockReason}），不发写帧");
        return CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.KeymapConfig,
            [ParamWriteSwitch, entry.Mode, entry.Modifier, entry.CodeLo]);
    }

    /// <summary>提交：`[03]`。</summary>
    public static byte[] EncodeCommit() =>
        CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.KeymapConfig, [ParamCommit]);

    /// <summary>通用应答解析：DATA = `[PARAM][STATUS]...`；返回 PARAM 回显与 STATUS。</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> data, out byte param, out byte status)
    {
        param = status = 0;
        if (data.Length < 2)
            return false;
        param = data[0];
        status = data[1];
        return true;
    }

    /// <summary>元信息应答：`[01][STATUS][count][max_entries][ver_hi][ver_lo]`（探测回包）。</summary>
    public static bool TryParseMeta(ReadOnlySpan<byte> data, out byte status, out int count, out int maxEntries, out int version)
    {
        status = 0;
        count = maxEntries = version = 0;
        if (data.Length < 7 || data[0] != ParamReadEntry)
            return false;
        status = data[1];
        count = data[2];
        maxEntries = data[3];
        version = (data[4] << 8) | data[5];
        return true;
    }
}
