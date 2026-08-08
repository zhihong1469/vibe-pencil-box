using PencilBox.Core.Protocol;
using PencilBox.Core.Communication;
using PencilBox.Core.Models;
using PencilBox.Core.Configuration;

namespace PencilBox.Tests;

public class ProtocolTests
{
    [Fact]
    public void CommandFrame_Build_EmptyPayload_ShouldHaveCorrectLength()
    {
        var frame = CommandFrame.Build(HidChannel.PcToDongle, CommandId.GetDongleMessage);

        Assert.Equal(CommandFrame.HeaderLength + CommandFrame.FooterLength, frame.Length);
        Assert.Equal(0x4A, frame[0]);
        Assert.Equal(0x4C, frame[1]);
        Assert.Equal(CommandFrame.ProtocolVersion, frame[2]);
        Assert.Equal((byte)HidChannel.PcToDongle, frame[3]);
        Assert.Equal(0, frame[4]); // length high byte
        Assert.Equal(0, frame[5]); // length low byte
        Assert.Equal((byte)CommandId.GetDongleMessage, frame[6]);
        Assert.Equal(CommandFrame.TailTag, frame[7]);
    }

    [Fact]
    public void CommandFrame_Build_WithData_ShouldHaveCorrectLength()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var frame = CommandFrame.Build(HidChannel.PcToDongle, CommandId.GetConnectDevice, data);

        Assert.Equal(CommandFrame.HeaderLength + data.Length + CommandFrame.FooterLength, frame.Length);

        Assert.Equal(data[0], frame[7]);
        Assert.Equal(data[1], frame[8]);
        Assert.Equal(data[2], frame[9]);
        Assert.Equal(CommandFrame.TailTag, frame[10]);
    }

    [Fact]
    public void CommandFrame_TryParse_ValidFrame_ShouldReturnTrue()
    {
        var data = new byte[] { 0x42 };
        var frame = CommandFrame.Build(HidChannel.PcToDongle, CommandId.GetDongleMessage, data);

        var reportData = new byte[frame.Length + 1];
        reportData[0] = 0xA0; // Report ID
        Array.Copy(frame, 0, reportData, 1, frame.Length);

        var result = CommandFrame.TryParse(reportData, out byte reportId,
            out HidChannel channel, out CommandId commandId, out byte[] payload);

        Assert.True(result);
        Assert.Equal((byte)0xA0, reportId);
        Assert.Equal(HidChannel.PcToDongle, channel);
        Assert.Equal(CommandId.GetDongleMessage, commandId);
        Assert.Single(payload);
        Assert.Equal((byte)0x42, payload[0]);
    }

    [Fact]
    public void CommandFrame_TryParse_InvalidHead_ShouldReturnFalse()
    {
        var frame = CommandFrame.Build(HidChannel.PcToDongle, CommandId.GetDongleMessage);
        frame[1] = 0xFF; // corrupt head

        var result = CommandFrame.TryParse(frame, out _, out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void CommandFrame_TryParse_InvalidTail_ShouldReturnFalse()
    {
        var frame = CommandFrame.Build(HidChannel.PcToDongle, CommandId.GetDongleMessage);
        frame[frame.Length - 1] = 0x00; // corrupt tail

        var result = CommandFrame.TryParse(frame, out _, out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void PencilBoxProtocol_BuildGetDongleMessage_ShouldReturnValidFrame()
    {
        var cmd = PencilBoxProtocol.BuildGetDongleMessage();

        Assert.NotEmpty(cmd);
        Assert.True(CommandFrame.IsValidFrame(cmd));
    }

    [Fact]
    public void PencilBoxProtocol_BuildDisconnectDevice_ShouldContainDeviceIndex()
    {
        byte deviceIndex = 0x05;
        var cmd = PencilBoxProtocol.BuildDisconnectDevice(deviceIndex);

        var reportData = new byte[cmd.Length + 1];
        reportData[0] = ReportIds.CustomCommand;
        Array.Copy(cmd, 0, reportData, 1, cmd.Length);

        var parsed = CommandFrame.TryParse(reportData, out _, out _, out var commandId, out var payload);
        Assert.True(parsed);
        Assert.Equal(CommandId.DisconnectDevice, commandId);
        Assert.Single(payload);
        Assert.Equal(deviceIndex, payload[0]);
    }

    [Fact]
    public void PencilBoxProtocol_ParseResponse_ValidFrame_ShouldReturnDecodedFrame()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = CommandFrame.Build(HidChannel.DongleToPc, CommandId.GetDongleMessage, data);

        var reportData = new byte[frame.Length + 1];
        reportData[0] = 0xA0;
        Array.Copy(frame, 0, reportData, 1, frame.Length);

        var result = PencilBoxProtocol.ParseResponse(reportData);

        Assert.NotNull(result);
        Assert.Equal(HidChannel.DongleToPc, result.Channel);
        Assert.Equal(CommandId.GetDongleMessage, result.CommandId);
        Assert.Equal(4, result.Payload.Length);
        Assert.Equal((byte)0x01, result.GetByte(0));
    }

    [Fact]
    public void DecodedFrame_GetUInt16_ShouldReturnCorrectValue()
    {
        var frame = new DecodedFrame(0xA0, HidChannel.DongleToPc, CommandId.GetDongleMessage,
            new byte[] { 0x01, 0x00, 0x0A });

        ushort value = frame.GetUInt16(0);
        Assert.Equal((ushort)0x0100, value);
    }

    [Fact]
    public void DecodedFrame_GetByte_OutOfRange_ShouldReturnZero()
    {
        var frame = new DecodedFrame(0xA0, HidChannel.DongleToPc, CommandId.GetDongleMessage,
            new byte[] { 0x01 });

        byte value = frame.GetByte(10);
        Assert.Equal((byte)0, value);
    }

    [Fact]
    public void CommandFrame_Build_DataExceedsMaxLength_ShouldThrow()
    {
        var data = new byte[CommandFrame.MaxDataLength + 1];

        Assert.Throws<ArgumentException>(() =>
            CommandFrame.Build(HidChannel.PcToDongle, CommandId.Custom, data));
    }
}

public class ModelTests
{
    [Fact]
    public void MacroConfig_DefaultValues_ShouldBeCorrect()
    {
        var macro = new MacroConfig();

        Assert.False(string.IsNullOrEmpty(macro.Id));
        Assert.True(macro.IsEnabled);
        Assert.NotNull(macro.KeySequence);
        Assert.Empty(macro.KeySequence);
        Assert.Equal(100, macro.KeyIntervalMs);
        Assert.Equal(0, macro.LoopCount);
    }

    [Fact]
    public void KeyAction_DefaultValues_ShouldBeCorrect()
    {
        var action = new KeyAction();

        Assert.Equal(KeyType.Keyboard, action.Type);
        Assert.Equal((ushort)0, action.KeyCode);
        Assert.Equal(100, action.DurationMs);
        Assert.Equal(ModifierKey.None, action.Modifiers);
    }

    [Fact]
    public void AudioFormat_DefaultValues_ShouldBeCorrect()
    {
        var format = new AudioFormat();

        Assert.Equal(16000, format.SampleRate);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal(1, format.Channels);
        Assert.Equal(AudioEncoding.Pcm, format.Encoding);
        Assert.Equal(256, format.SamplesPerFrame);
    }

    [Fact]
    public void AudioFormat_BytesPerSecond_ShouldCalculateCorrectly()
    {
        var format = new AudioFormat
        {
            SampleRate = 48000,
            BitsPerSample = 16,
            Channels = 2
        };

        Assert.Equal(48000 * 16 * 2 / 8, format.BytesPerSecond);
    }

    [Fact]
    public void HidDeviceInfo_ToString_ShouldContainVidPid()
    {
        var device = new HidDeviceInfo
        {
            VendorId = 0x1234,
            ProductId = 0x5678,
            ProductName = "Test Device"
        };

        var str = device.ToString();
        Assert.Contains("VID_1234", str);
        Assert.Contains("PID_5678", str);
    }

    [Fact]
    public void ConnectedDevice_DefaultState_ShouldBeConnected()
    {
        var device = new ConnectedDevice
        {
            Index = 1,
            MacAddress = "00:11:22:33:44:55",
            State = ConnectionState.Connected
        };

        Assert.Equal(1, device.Index);
        Assert.Equal("00:11:22:33:44:55", device.MacAddress);
        Assert.Equal(ConnectionState.Connected, device.State);
    }
}

public class ConfigTests
{
    [Fact]
    public void AppSettings_DefaultValues_ShouldBeCorrect()
    {
        var settings = new AppSettings();

        Assert.Equal(5000, settings.HidTimeoutMs);
        Assert.False(settings.AutoConnect);
        Assert.Equal(LogLevel.Information, settings.LogLevel);
    }

    [Fact]
    public void LogLevel_EnumValues_ShouldBeDefined()
    {
        Assert.Equal(6, Enum.GetValues(typeof(LogLevel)).Length);
        Assert.True(Enum.IsDefined(typeof(LogLevel), LogLevel.Trace));
        Assert.True(Enum.IsDefined(typeof(LogLevel), LogLevel.Critical));
    }
}
