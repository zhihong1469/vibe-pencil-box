namespace PencilBox.Core.Models;

/// <summary>
/// Python 环境信息
/// </summary>
public record DspEnvironmentInfo(
    bool PythonAvailable,
    string PythonVersion,
    string PythonPath,
    Dictionary<string, bool> Packages,
    List<string> Errors);

/// <summary>
/// DSP 脚本信息
/// </summary>
public record DspScriptInfo(
    string Name,
    string Description,
    List<DspParamInfo> Parameters,
    bool SupportsStreaming);

/// <summary>
/// 脚本参数信息
/// </summary>
public record DspParamInfo(
    string Name,
    string Type,
    string Description,
    object? DefaultValue);

/// <summary>
/// DSP 执行请求
/// </summary>
public record DspRequest(
    string ScriptName,
    string? InputFilePath,
    byte[]? InputData,
    AudioFormat? InputFormat,
    Dictionary<string, object>? Parameters);

/// <summary>
/// DSP 执行结果
/// </summary>
public record DspResult(
    bool Success,
    Dictionary<string, object>? Data,
    string? OutputFilePath,
    string? Error,
    TimeSpan Duration);

/// <summary>
/// 一键分析选项
/// </summary>
public record DspAnalysisOptions(
    int? MaxFirTaps = 64,
    int SampleRate = 16000,
    bool GeneratePlots = false,
    bool IncludeParamPackage = true);

/// <summary>
/// 一键分析综合报告
/// </summary>
public record DspFullReport(
    DspResult? WaveformStats,
    DspResult? FftSpectrum,
    DspResult? VadResult,
    DspResult? FilterDesign,
    DspResult? AgcRecommendation,
    byte[]? DeviceParamsPackage,
    string Summary);

/// <summary>
/// 执行进度事件
/// </summary>
public record DspProgressEventArgs(
    string CurrentStep,
    int Percent,
    string? Message);
