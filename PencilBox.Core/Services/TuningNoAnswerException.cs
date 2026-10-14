// <copyright file="TuningNoAnswerException.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Services;

/// <summary>
/// `0x61`/`0x62` 请求**无应答**（超时重发耗尽）——区别于其他失败的**专用类型**。
/// 为什么不用消息文本匹配：Desktop 工作台曾按 `Message.Contains("无应答")` 把它当链路丢失
/// （LinkState=Lost + 弹"连接丢失"），而笔端未实现 0x61/0x62 阶段无应答 100% 发生且**属预期**、
/// 串口本体未断——字符串启发式把"命令不支持"误判成"链路断"。UI 按本类型分流：保持 Connected，只亮错误。
/// </summary>
public class TuningNoAnswerException : InvalidOperationException
{
    public TuningNoAnswerException(string message) : base(message)
    {
    }
}
