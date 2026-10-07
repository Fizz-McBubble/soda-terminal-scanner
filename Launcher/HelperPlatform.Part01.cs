using System.ComponentModel;
using System.Management;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ZZZScannerHelper;

internal static class HelperErrors
{
    public static HelperErrorMessage FromException(Exception exception, string fallbackPhase)
    {
        var failure = exception as HelperFailureException;
        var phase = failure?.Phase ?? fallbackPhase;
        var code = failure?.Code ?? exception switch
        {
            HttpRequestException => phase == "manifest" ? "manifest_unreachable" : "download_failed",
            UnauthorizedAccessException => "install_access_denied",
            InvalidDataException => phase == "manifest" ? "manifest_invalid" : "package_corrupt",
            IOException => "install_failed",
            System.Net.HttpListenerException listener when listener.ErrorCode == 5 => "listener_access_denied",
            System.Net.HttpListenerException => "port_in_use",
            Win32Exception win32 when win32.NativeErrorCode == 1223 => "uac_cancelled",
            _ => $"{phase}_failed"
        };
        var diagnosticId = HelperLog.RecordException(code, phase, exception);
        var retryable = failure?.Retryable ?? code is not ("unsupported_os" or "unsupported_arch");
        var actions = new List<HelperErrorAction>();
        if (retryable)
        {
            actions.Add(new HelperErrorAction { Kind = "retry", Label = "重试" });
        }

        if (code is "package_corrupt" or "install_failed" or "install_access_denied")
        {
            actions.Add(new HelperErrorAction { Kind = "repair", Label = "重新下载并修复" });
        }

        actions.Add(new HelperErrorAction { Kind = "open_logs", Label = "打开日志目录" });
        actions.Add(new HelperErrorAction { Kind = "copy_diagnostics", Label = "复制诊断信息" });
        return new HelperErrorMessage
        {
            Code = code,
            Phase = phase,
            Title = failure?.Title ?? DefaultTitle(code),
            Message = failure?.Message ?? exception.Message,
            Remedy = failure?.Remedy ?? DefaultRemedy(code),
            Retryable = retryable,
            Actions = actions,
            DiagnosticId = diagnosticId,
            Details = failure?.Details ?? []
        };
    }

    private static string DefaultTitle(string code) => code switch
    {
        "manifest_unreachable" => "无法获取扫描器版本信息",
        "manifest_invalid" => "扫描器版本信息无效",
        "download_failed" => "扫描器下载失败",
        "package_corrupt" => "扫描器安装包校验失败",
        "install_access_denied" => "没有权限安装扫描器",
        "uac_cancelled" => "已取消管理员授权",
        "native_dependency_missing" => "扫描器缺少运行组件",
        "child_exited" => "扫描器启动后立即退出",
        "child_handshake_timeout" => "扫描器启动超时",
        "port_in_use" => "扫描助手端口被占用",
        "listener_access_denied" => "扫描助手无法监听本机地址",
        _ => "OCR 扫描器准备失败"
    };

    private static string DefaultRemedy(string code) => code switch
    {
        "manifest_unreachable" or "download_failed" => "请检查网络后重试；Helper 会自动切换备用下载地址。",
        "package_corrupt" => "请选择“重新下载并修复”，Helper 会清除损坏缓存后重新安装。",
        "install_access_denied" => "请检查安全软件或受控文件夹访问设置，然后重新修复。",
        "uac_cancelled" => "需要扫描提升权限的游戏时，请重新选择管理员启动并确认 UAC。",
        "native_dependency_missing" => "请选择“重新下载并修复”；所需 VC 组件应已包含在扫描器包中。",
        "port_in_use" => "请关闭其他扫描助手实例；如果仍无法启动，请重启电脑后再运行 Helper。",
        "listener_access_denied" => "请检查本机安全软件或受管电脑的策略是否阻止扫描助手监听 127.0.0.1:43127，并联系电脑管理员；无需更改防火墙或以管理员身份运行扫描助手。",
        _ => "请重试；如果问题持续，请打开日志并提供诊断编号。"
    };
}
