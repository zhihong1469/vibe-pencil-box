// <copyright file="SerialScopeTests.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Text;
using PencilBox.Core.Protocol.Company;
using PencilBox.Core.SerialScope;

namespace PencilBox.Tests;

/// <summary>
/// 串口上位机（serial scope）**黄金向量**测试 —— 离线、不依赖硬件（05 §4 S1 验收）。
///
/// 样本全部取自**实测数据**：
/// - 分域样本 = `serial-chat/logs/ota-backup/02-ota-full_DONGLE.log`（USB OTA 全流程）；
/// - 帧样本 = 骨架 §6.1 的权威 `LEN` 口径（`0x16/01` 总 20B、`0x16/07` 总 60B）。
/// </summary>
public class SerialScopeTests
{
    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>`0x16/01` 会话开始帧（总 20B）：`LEN=0x10`，`DCMD=01`，`FILELEN=229298`（⇒224 块），`CRC=0x1C4D`</summary>
    private static byte[] OtaSessionStartFrame() =>
        Bytes("F5 AA AA 10 61 01 16 01 00 01 01 01 00 00 1C 4D 00 03 7F B2");

    /// <summary>`0x16/07` 数据片（总 60B）：`LEN=0x38`，`packh=0x0C`、`packl=0x07`，`DATA48` 由调用方给定</summary>
    private static byte[] OtaChunkFrame(byte[] data48) =>
        Bytes("F5 AA AA 38 61 01 16 07 00 00 0C 07").Concat(data48).ToArray();

    // ================= 分域：设备日志 → 域标签 =================

    [Theory]
    // 升级内部（真实域优先于 [TODO] 标记）
    [InlineData("[19:33:15.447] [00:40:52.936][Info]: [DONGLE][TODO] ota_usb: dual_bank_update_allow_check(size=229298)", LogDomain.OtaUsb)]
    [InlineData("[19:33:15.448] [00:40:52.939][Info]: [DONGLE][ota_usb] RUNNING: request packh=0/224", LogDomain.OtaUsb)]
    [InlineData("[19:34:28.731] [00:42:06.214][Info]: [DONGLE][ota_usb] DONE -> cpu_reset in 100ms", LogDomain.OtaUsb)]
    [InlineData("[19:34:28.824] [00:42:06.316][Info]: [DONGLE][ota_usb] cpu_reset() -> boot new bank", LogDomain.OtaUsb)]
    [InlineData("[19:34:29.004] [00:42:06.5.0][Info]: [DONGLE][ota_usb] block 0 done (1024/229298 bytes)", LogDomain.OtaUsb)]
    // F5 协议
    [InlineData("[19:33:15.447] [00:40:52.935][Info]: [DONGLE][f5] rx cmdtype=0x16 sub=0x01 len=0x10", LogDomain.F5)]
    [InlineData("[19:34:29.056] [00:00:00.272][Info]: [DONGLE][f5] golden selftest: PASS (err=0)", LogDomain.F5)]
    // BLE 扫描（大写形态归一为小写）
    [InlineData("[19:31:17.339] [00:38:54.831][Info]: [GATT_CLIENT]remoter_name:U-HDMCF93 ,rssi:-95", LogDomain.GattClient)]
    // 配对
    [InlineData("[00:00:01.000][Info]: [DONGLE][pair] enter pairing mode", LogDomain.Pair)]
    // 只有标记、没有域 ⇒ todo
    [InlineData("[00:00:02.000][Info]: [DONGLE][TODO] dual_bank_burn_boot_info() not wired yet", LogDomain.Todo)]
    [InlineData("[00:00:02.000][Info]: [DONGLE][STUB] passive_update_init()", LogDomain.Todo)]
    // JTAG 二进制混入 / 记号行 ⇒ other（05 §5.4 默认折叠）
    [InlineData("36 8B D7 AC 0D FA ", LogDomain.Other)]
    [InlineData("^^", LogDomain.Other)]
    [InlineData("^", LogDomain.Other)]
    public void Classifier_AssignsDomain(string line, string expected)
    {
        Assert.Equal(expected, new DeviceLogClassifier().Classify(line));
    }

    [Fact]
    public void Classifier_EmptyLine_IsOther()
    {
        Assert.Equal(LogDomain.Other, new DeviceLogClassifier().Classify("   "));
    }

    // ================= 解码：F5 帧 → 人类可读 =================

    [Fact]
    public void Decoder_ReadVersion_Request_IsDownlink()
    {
        // `F5AAAA0461010300`：LEN=0x04（权威口径）
        Assert.Equal("↓0x03 读版本", new CompanyFrameDecoder().Decode(Bytes("F5AAAA0461010300")));
    }

    [Fact]
    public void Decoder_ReadVersion_Response_ShowsModelAndSver()
    {
        // STA=00 MODEL=00 SVER=0.44.64（对应实测基站版本）
        var decoded = new CompanyFrameDecoder().Decode(Bytes("F5AAAA08E101030000002C40"));

        Assert.Equal("↑0x03 应答 STA=00 MODEL=00(基站) SVER=0.44.64", decoded);
    }

    [Fact]
    public void Decoder_OtaSession_Start_RecordsTotalBlocks()
    {
        // 0x16/01：LEN=0x10（总 20B）；FILELEN=229298 ⇒ 224 块（与实测一致）
        var decoded = new CompanyFrameDecoder().Decode(OtaSessionStartFrame());

        Assert.Equal("↓0x16 开始 DCMD=01 DOWNTYPE=00/基站 HVER=01 SVER=1.0.0 FILELEN=229298 块 224 CRC=0x1C4D",
            decoded);
    }

    [Fact]
    public void Decoder_OtaChunk_RendersPackhWithTotalFromSession()
    {
        var decoder = new CompanyFrameDecoder();

        // 先给会话开始帧（块总数 224），再看数据片能否渲染成 `packh=12/224`
        decoder.Decode(OtaSessionStartFrame());

        // 0x16/07：LEN=0x38（总 60B）；DATA48 全 0
        Assert.Equal("↓0x16 数据块 packh=12/224 packl=07", decoder.Decode(OtaChunkFrame(new byte[48])));
    }

    [Fact]
    public void Decoder_OtaChunk_WithoutSession_OmitsTotal()
    {
        // 没经过 0x16/01 ⇒ 不知道总数，只给块号（不编造分母）
        Assert.Equal("↓0x16 数据块 packh=12 packl=07",
            new CompanyFrameDecoder().Decode(OtaChunkFrame(new byte[48])));
    }

    [Fact]
    public void Decoder_OtaBlockRequest_IsUplink()
    {
        var decoder = new CompanyFrameDecoder();
        decoder.Decode(OtaSessionStartFrame());

        // 0x16/0A：LEN=0x08（总 12B）
        Assert.Equal("↑0x16 请求块 packh=12/224 DOWNTYPE=00/基站",
            decoder.Decode(Bytes("F5 AA AA 08 E1 01 16 0A 00 00 0C 00")));
    }

    [Fact]
    public void Decoder_OtaProgress_TerminalState_HasVerdict()
    {
        var decoder = new CompanyFrameDecoder();
        decoder.Decode(OtaSessionStartFrame());

        // 0x16/20：LEN=0x07（总 11B）；PACKH=0xE0=224 ⇒ 224/224；DCMD=02 完成
        Assert.Equal("↑0x16 进度 packh=224/224 终态=完成 DOWNTYPE=00/基站",
            decoder.Decode(Bytes("F5 AA AA 07 E1 01 16 20 00 02 E0")));

        // DCMD=03 失败：`PACKH` 是**原因**不是块号 ⇒ 不能渲染成假进度
        Assert.Equal("↑0x16 进度 终态=失败（原因=0E） DOWNTYPE=00/基站",
            decoder.Decode(Bytes("F5 AA AA 07 E1 01 16 20 00 03 0E")));
    }

    [Fact]
    public void Decoder_PairEnter_Response02_ShowsMacAndName()
    {
        var data = new List<byte> { PairEnterStatus.Success };
        data.AddRange(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF });
        data.AddRange(Name32("SunVoteR1:AABBCCDDEEFF"));

        var frame = CompanyFrame.Build(CompanyFrame.BaseCmdResponse, CompanyCmd.PairEnter, data.ToArray());

        Assert.Equal("↑0x52 应答 STA=02 配对成功 MAC=AA:BB:CC:DD:EE:FF NAME=\"SunVoteR1:AABBCCDDEEFF\"",
            new CompanyFrameDecoder().Decode(frame));
    }

    [Fact]
    public void Decoder_UnknownCmdType_ReturnsNull_DoesNotInventSemantics()
    {
        Assert.Null(new CompanyFrameDecoder().Decode(Bytes("F5AAAA0461019900")));
        Assert.Null(new CompanyFrameDecoder().Decode(Bytes("00" + "11AA04610103")));
    }

    // ================= 拆分：字节流 → 文本行 / 帧 =================

    [Fact]
    public void Demuxer_SeparatesTextFromFrame_EvenIfFrameContainsNewlines()
    {
        // DATA48 末尾两个字节填 0x0A：按 `\n` 切行的朴素做法会把这一帧劈成多段垃圾文本
        var frame = OtaChunkFrame(new byte[46].Concat(new byte[] { 0x0A, 0x0A }).ToArray());

        var stream = new List<byte>();
        stream.AddRange(Encoding.ASCII.GetBytes("hello\r\nworld\n"));
        stream.AddRange(frame);
        stream.AddRange(Encoding.ASCII.GetBytes("tail\n"));

        var demuxer = new StreamDemuxer();
        var events = demuxer.Append(stream.ToArray());

        Assert.Equal(4, events.Count);
        Assert.Equal(StreamEventKind.TextLine, events[0].Kind);
        Assert.Equal("hello", events[0].Text);            // `\r` 已丢
        Assert.Equal(StreamEventKind.TextLine, events[1].Kind);
        Assert.Equal("world", events[1].Text);
        Assert.Equal(StreamEventKind.Frame, events[2].Kind);
        Assert.Equal(frame, events[2].Frame);
        Assert.Equal(StreamEventKind.TextLine, events[3].Kind);
        Assert.Equal("tail", events[3].Text);
    }

    [Fact]
    public void Demuxer_ReassemblesFrameAcrossChunks()
    {
        var frame = Bytes("F5AAAA08E101030000002C40");
        var demuxer = new StreamDemuxer();

        Assert.Empty(demuxer.Append(frame.AsSpan(0, 5)));

        var events = demuxer.Append(frame.AsSpan(5));
        Assert.Single(events);
        Assert.Equal(StreamEventKind.Frame, events[0].Kind);
        Assert.Equal(frame, events[0].Frame);
    }

    [Fact]
    public void Demuxer_SkipsInvalidLenAndStillFindsFollowingFrame()
    {
        var bad = Bytes("F5AAAA026101");                     // LEN=0x02（非法）
        var good = CompanyFrame.Build(CompanyFrame.BaseCmdRequest, CompanyCmd.ReadVersion, new byte[] { 0x00 });

        var events = new StreamDemuxer().Append(bad.Concat(good).ToArray());

        Assert.Single(events);
        Assert.Equal(StreamEventKind.Frame, events[0].Kind);
        Assert.Equal(good, events[0].Frame);
    }

    [Fact]
    public void Demuxer_PureTextStream_IsUntouched()
    {
        var text = Encoding.ASCII.GetBytes("[00:40:52.939][Info]: [DONGLE][ota_usb] RUNNING: request packh=0/224\n");

        var events = new StreamDemuxer().Append(text);

        Assert.Single(events);
        Assert.Equal(StreamEventKind.TextLine, events[0].Kind);
        Assert.Equal("[00:40:52.939][Info]: [DONGLE][ota_usb] RUNNING: request packh=0/224", events[0].Text);
    }

    [Fact]
    public void Demuxer_StripsAnsiEscape()
    {
        var text = Encoding.ASCII.GetBytes("\u001b[32mPASS\u001b[0m\n");

        var events = new StreamDemuxer().Append(text);

        Assert.Equal("PASS", events[0].Text);
    }

    [Fact]
    public void Demuxer_Flush_EmitsTrailingPartialLine()
    {
        var demuxer = new StreamDemuxer();
        Assert.Empty(demuxer.Append(Encoding.ASCII.GetBytes("no newline at end")));

        var events = demuxer.Flush();
        Assert.Single(events);
        Assert.Equal("no newline at end", events[0].Text);
    }

    // ================= 管线：帧行呈现 + 分域 =================

    [Fact]
    public void Pipeline_FrameLine_HasFramePrefixAndF5Domain()
    {
        var pipeline = new SerialScopePipeline("PENCIL");
        var frame = OtaChunkFrame(new byte[48]);
        var lines = pipeline.Feed(DateTimeOffset.Parse("2026-09-15T19:33:15.447+08:00"), frame);

        var line = Assert.Single(lines);
        Assert.Equal(LogSource.Frame, line.Source);
        Assert.Equal(LogDomain.F5, line.Domain);
        Assert.Equal(Hex(frame), line.Raw);
        Assert.Equal("[FRAME] ↓0x16 数据块 packh=12 packl=07", line.Decoded);
    }

    [Fact]
    public void Pipeline_DecodeOff_KeepsRawFrameEvidence()
    {
        var pipeline = new SerialScopePipeline("PENCIL", decodeFrames: false);
        var frame = Bytes("F5AAAA08E101030000002C40");

        var line = Assert.Single(pipeline.Feed(DateTimeOffset.UnixEpoch, frame));

        Assert.Equal("[FRAME] RAW " + Hex(frame), line.Decoded);   // 仍定帧、不搅乱文本，只是不解码
    }

    [Fact]
    public void Pipeline_TextLine_CarriesTimestampAndDomain()
    {
        var pipeline = new SerialScopePipeline("DONGLE");
        var ts = DateTimeOffset.Parse("2026-09-15T19:34:28.731+08:00");

        var lines = pipeline.Feed(ts,
            Encoding.ASCII.GetBytes("[00:42:06.214][Info]: [DONGLE][ota_usb] DONE -> cpu_reset in 100ms\n"));

        var line = Assert.Single(lines);
        Assert.Equal(ts, line.Timestamp);
        Assert.Equal("DONGLE", line.PortName);
        Assert.Equal(LogSource.Text, line.Source);
        Assert.Equal(LogDomain.OtaUsb, line.Domain);
        Assert.Equal("[00:42:06.214][Info]: [DONGLE][ota_usb] DONE -> cpu_reset in 100ms", line.Decoded);
    }

    // ================= 轮转策略 =================

    [Fact]
    public void RotationPolicy_RotatesBeforeOverflow_SoLatestLineSurvives()
    {
        var policy = new LogRotationPolicy(maxBytes: 100, backupCount: 10);

        Assert.False(policy.ShouldRotate(currentBytes: 98, incomingBytes: 0));
        Assert.False(policy.ShouldRotate(currentBytes: 100, incomingBytes: 0));   // 正好装满，仍可写
        Assert.True(policy.ShouldRotate(currentBytes: 100, incomingBytes: 1));    // 再写 1B 就超
        Assert.True(policy.ShouldRotate(currentBytes: 50, incomingBytes: 80));
    }

    [Theory]
    [InlineData("10M:10", 10L * 1024 * 1024, 10)]
    [InlineData("512K:3", 512L * 1024, 3)]
    [InlineData("2048", 2048, 10)]                 // 缺份数 ⇒ 默认 10
    [InlineData(null, 10L * 1024 * 1024, 10)]      // 缺省 ⇒ 默认值
    public void RotationPolicy_ParsesCliText(string? text, long expectedBytes, int expectedBackups)
    {
        Assert.True(LogRotationPolicy.TryParse(text, out var policy));
        Assert.Equal(expectedBytes, policy.MaxBytes);
        Assert.Equal(expectedBackups, policy.BackupCount);

        Assert.Equal("serial-scope.log.3", LogRotationPolicy.BackupName("serial-scope.log", 3));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("10M:0")]
    [InlineData("10M:-1")]
    [InlineData("0")]
    [InlineData("10M:x")]
    public void RotationPolicy_RejectsBadText(string text)
    {
        Assert.False(LogRotationPolicy.TryParse(text, out _));
    }

    // ================= 会话时间轴（多口合并） =================

    [Fact]
    public void SessionAssembler_KeepsRecentLinesAndPerPortCounts()
    {
        var assembler = new SessionAssembler(capacity: 3);

        for (int i = 0; i < 5; i++)
        {
            assembler.Add(new LogLine(DateTimeOffset.UnixEpoch.AddMilliseconds(i), "DONGLE",
                LogSource.Text, LogDomain.OtaUsb, $"line{i}", $"line{i}"));
        }
        assembler.Add(new LogLine(DateTimeOffset.UnixEpoch.AddMilliseconds(9), "PENCIL",
            LogSource.Frame, LogDomain.F5, "F5AAAA0461010300", "[FRAME] ↓0x03 读版本"));

        Assert.Equal(6, assembler.TotalCount);
        Assert.Equal(3, assembler.Recent(10).Count);              // 环形缓冲只留最近 3 行
        Assert.Equal((1L, 1L), assembler.CountOf("PENCIL"));      // 1 行 = 1 帧
        Assert.Equal((5L, 0L), assembler.CountOf("DONGLE"));
    }

    [Fact]
    public void SessionAssembler_RecentIsTimeOrdered()
    {
        var assembler = new SessionAssembler();
        assembler.Add(new LogLine(DateTimeOffset.UnixEpoch.AddSeconds(2), "B", LogSource.Text, LogDomain.Other, "b", "b"));
        assembler.Add(new LogLine(DateTimeOffset.UnixEpoch.AddSeconds(1), "A", LogSource.Text, LogDomain.Other, "a", "a"));

        var recent = assembler.Recent(10);
        Assert.Equal(new[] { "a", "b" }, recent.Select(l => l.Decoded).ToArray());
    }

    // ================= 端口角色识别 =================

    [Theory]
    [InlineData("JLVirtualJtagSerial (COM6)", SerialPortRole.JtagVirtual)]
    [InlineData("USB-SERIAL CH340 (COM7)", SerialPortRole.UsbBridge)]
    [InlineData("Silicon Labs CP210x USB to UART Bridge (COM20)", SerialPortRole.UsbBridge)]
    [InlineData("标准蓝牙串行链路 (COM9)", SerialPortRole.Bluetooth)]
    [InlineData("", SerialPortRole.Unknown)]
    public void PortClassifier_IdentifiesRole(string description, SerialPortRole expected)
    {
        Assert.Equal(expected, SerialPortClassifier.Classify(description));
    }

    [Fact]
    public void PortClassifier_OnlyWarnsForTrapPorts()
    {
        Assert.NotNull(SerialPortClassifier.SelectionWarning(SerialPortRole.JtagVirtual));
        Assert.NotNull(SerialPortClassifier.SelectionWarning(SerialPortRole.Bluetooth));
        Assert.Null(SerialPortClassifier.SelectionWarning(SerialPortRole.UsbBridge));   // 正常口不打扰
    }

    [Fact]
    public void PortClassifier_PrefersOurPortsOverBluetooth()
    {
        // 回归：参考实现把"调试器虚拟口"排最后 ⇒ 自动挑口会跳过我们自己的 Dongle 日志口
        //（JLVirtualJtagSerial / COM6）去选蓝牙口。本产品的偏好必须反过来。
        var roles = new[]
        {
            SerialPortRole.Bluetooth, SerialPortRole.Unknown,
            SerialPortRole.JtagVirtual, SerialPortRole.UsbBridge
        };

        var ordered = roles.OrderBy(SerialPortClassifier.Preference).ToArray();

        Assert.Equal(
            new[]
            {
                SerialPortRole.UsbBridge, SerialPortRole.JtagVirtual,
                SerialPortRole.Unknown, SerialPortRole.Bluetooth
            },
            ordered);
    }

    // ================= 小工具 =================

    /// <summary>定长 32B 的 `BT_NAME`（ASCII 截断 + 0 补齐）</summary>
    private static byte[] Name32(string name)
    {
        var buffer = new byte[CompanyCodec.BtNameLength];
        var bytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(bytes, buffer, Math.Min(bytes.Length, buffer.Length));
        return buffer;
    }
}
