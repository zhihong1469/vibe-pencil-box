// <copyright file="CompanyPenStatusTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using PencilBox.Core.Protocol.Company;

namespace PencilBox.Tests;

/// <summary>
/// 协议补充 05（`0x53` 状态上行）/ 01（`0x51 PARAM=03` 清绑定）的 codec 单测。
/// 黄金向量来自补充正文字节定义：拉取 `F5 AA AA 04 61 01 53 01`、
/// 应答示例 `F5 AA AA 09 E1 01 53 00 04 01 00 02 5F`（S=2，`01 00`=DEV_STATE 正常、`02 5F`=BATT 95%）。
/// </summary>
public class CompanyPenStatusTests
{
    private readonly CompanyCodec _codec = new();

    // ================= 0x53 编码（黄金向量） =================

    [Fact]
    public void EncodePenStatusPull_GoldenVector()
    {
        var frame = _codec.EncodePenStatusPull();
        Assert.Equal(
            new byte[] { 0xF5, 0xAA, 0xAA, 0x04, 0x61, 0x01, 0x53, 0x01 },
            frame);
    }

    // ================= 0x53 解析：正常快照（正文示例） =================

    [Fact]
    public void TryParsePenStatusResp_DocExample_DecodesSlots()
    {
        // STATUS=00 BYTE2=0x04（src 硬件态/mode 0，统一TX §3.2：bit2=src、bit7-3 保留 0）
        // [01 00]=DEV_STATE 正常 [02 5F]=BATT 95%。
        // 勘误：补充 05 正文示例写 "BYTE2=0x40"，bit6 落保留区与 §3.2 位表矛盾——
        // 判定为 0x04 的十六进制换位笔误，PC 按规范位表解析（回填时向笔/键盘端同步）。
        var ok = _codec.TryParsePenStatusResp(
            new byte[] { 0x00, 0x04, 0x01, 0x00, 0x02, 0x5F }, out var s);

        Assert.True(ok);
        Assert.Equal(0x00, s.Status);
        Assert.Equal(0x04, s.Byte2);
        Assert.Equal(0, s.Mode);
        Assert.Equal(1, s.Src);
        Assert.Equal(2, s.Slots.Count);
        Assert.Equal(PenStateSlotId.DevState, s.Slots[0].Id);
        Assert.Equal(PenDevState.Normal, s.Slots[0].Value);
        Assert.Equal(PenStateSlotId.Batt, s.Slots[1].Id);
        Assert.Equal(0x5F, s.Slots[1].Value);   // 95%
    }

    [Fact]
    public void TryParsePenStatusResp_UnknownSlot_ForwardCompatible()
    {
        // 未知 id 0x05：原样收进快照不丢弃（补充 05 §6 前向兼容）
        var ok = _codec.TryParsePenStatusResp(
            new byte[] { 0x00, 0x00, 0x05, 0xAA }, out var s);

        Assert.True(ok);
        var slot = s.Find(0x05);
        Assert.NotNull(slot);
        Assert.Equal(0xAA, slot!.Value);
        Assert.Contains("未知 id", PenStatusText.SlotText(slot));
    }

    // ================= 0x53 解析：断连推送（STATUS=02 无槽区） =================

    [Fact]
    public void TryParsePenStatusResp_DisconnectPush_NoSlots()
    {
        var ok = _codec.TryParsePenStatusResp(new byte[] { 0x02 }, out var s);

        Assert.True(ok);
        Assert.Equal(PenStatusStatus.PenNotConnected, s.Status);
        Assert.Equal(0, s.Byte2);
        Assert.Empty(s.Slots);
    }

    // ================= 0x53 解析：非法结构 =================

    [Theory]
    [InlineData(new byte[0])]                    // 空载荷
    [InlineData(new byte[] { 0x00, 0x40, 0x01 })] // 槽区奇数长（半槽=损坏）
    public void TryParsePenStatusResp_Illegal_ReturnsFalse(byte[] data)
    {
        Assert.False(_codec.TryParsePenStatusResp(data, out _));
    }

    // ================= 0x51 PARAM=03 清绑定 =================

    [Fact]
    public void EncodePairClear_GoldenVector()
    {
        var frame = _codec.EncodePairClear();
        Assert.Equal(
            new byte[] { 0xF5, 0xAA, 0xAA, 0x04, 0x61, 0x01, 0x51, 0x03 },
            frame);
    }

    [Theory]
    [InlineData(new byte[] { 0x03, 0x00 }, 0x00)]   // 受理（幂等，未绑定也 00）
    [InlineData(new byte[] { 0x03, 0xFF }, 0xFF)]   // 升级会话中拒绝
    public void TryParsePairClearResp_StatusEcho(byte[] data, byte expected)
    {
        var ok = _codec.TryParsePairClearResp(data, out var status);

        Assert.True(ok);
        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData(new byte[0])]                          // 缺载荷
    [InlineData(new byte[] { 0x03 })]                  // 缺 STATUS
    [InlineData(new byte[] { 0x02, 0x00 })]            // PARAM≠03（不是清绑定应答）
    public void TryParsePairClearResp_Illegal_ReturnsFalse(byte[] data)
    {
        Assert.False(_codec.TryParsePairClearResp(data, out _));
    }

    // ================= 帧底座往返（E1 01 53 包装一致性） =================

    [Fact]
    public void PenStatus_FrameRoundTrip()
    {
        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PenStatus,
            new byte[] { 0x00, 0x04, 0x01, 0x00, 0x02, 0x5F });

        // LEN = 3 + 6 = 9（正文 LEN=5+S×2 口径）
        Assert.Equal(9, frame[3]);
        Assert.Equal(0xE1, frame[4]);
        Assert.Equal(0x01, frame[5]);
        Assert.Equal(0x53, frame[6]);

        var ok = CompanyFrame.TryParse(frame, out var baseCmd, out var type, out var data);
        Assert.True(ok);
        Assert.Equal(CompanyFrame.BaseCmdResponse, baseCmd);
        Assert.Equal(CompanyCmd.PenStatus, type);
        Assert.True(_codec.TryParsePenStatusResp(data, out var s));
        Assert.Equal(95, s.Find(PenStateSlotId.Batt)!.Value);
    }

    // ================= 展示文本 =================

    [Fact]
    public void PenStatusText_ModesAndSlots()
    {
        // 显示命名（专业叫法定版）：mode 0=待机；DEV_STATE 0x00=正常 / 0x01=休眠
        Assert.Equal("待机(IDLE)", PenStatusText.ModeText(0));
        Assert.Equal("识别(CAPTURING)", PenStatusText.ModeText(1));
        Assert.Equal("扩音(AMPLIFYING)", PenStatusText.ModeText(2));
        Assert.Equal("DEV_STATE=正常", PenStatusText.SlotText(new PenStatusSlot(PenStateSlotId.DevState, 0)));
        Assert.Equal("DEV_STATE=休眠", PenStatusText.SlotText(new PenStatusSlot(PenStateSlotId.DevState, 1)));
        Assert.Equal("BATT=95%", PenStatusText.SlotText(new PenStatusSlot(PenStateSlotId.Batt, 95)));
    }
}
