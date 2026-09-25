// <copyright file="SerialPortIo.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// <see cref="ISerialPortIo"/> 的实现：包一个 <see cref="SerialTransport"/>（正式路径 + UMDF 裸句柄兜底都在里面）。
/// 一个实例 = 一个物理口句柄，由 <see cref="SerialPortHub"/> 按口创建（06 §4.1：DTR/RTS 纪律与兜底收口在唯一开口点）。
/// </summary>
public sealed class SerialPortIo : ISerialPortIo
{
    private readonly SerialTransport _transport;

    public SerialPortIo(ILoggerFactory loggerFactory)
    {
        _transport = new SerialTransport(loggerFactory.CreateLogger<SerialTransport>());
        _transport.DataReceived += OnDataReceived;
    }

    private void OnDataReceived(object? sender, SerialDataReceivedEventArgs e)
        => Received?.Invoke(this, (ReadOnlyMemory<byte>)e.Data);

    public event EventHandler<ReadOnlyMemory<byte>>? Received;

    public bool IsOpen => _transport.IsConnected;

    public Task<bool> OpenAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
        => _transport.ConnectAsync(portName, new SerialPortSettings { BaudRate = baudRate }, cancellationToken);

    public Task CloseAsync() => _transport.DisconnectAsync();

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => _transport.SendAsync(data.ToArray(), cancellationToken);
}
