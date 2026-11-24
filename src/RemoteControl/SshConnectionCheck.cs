using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class SshConnectionCheck
{
    internal const string LoginSuccess = "SSH 登录成功。";
    const string PasswordVariable = "ISCREENX_SSH_ASKPASS_SECRET";

    // Windows OpenSSH launches this executable as SSH_ASKPASS with a single prompt.
    internal static bool AnswerPasswordPrompt(string[] args)
    {
        string password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (password == null) return false;
        if (args.Length != 1 || !args[0].TrimEnd().EndsWith("password:", StringComparison.OrdinalIgnoreCase)) {
            Environment.ExitCode = 1;
            return true;
        }
        byte[] answer = Encoding.UTF8.GetBytes(password + "\n");
        try { using (var output = Console.OpenStandardOutput()) output.Write(answer, 0, answer.Length); }
        finally { Array.Clear(answer, 0, answer.Length); }
        return true;
    }

    internal static string Run(IPAddress address, int port, string password)
    {
        if (address == null) return "请先连接手机投屏。";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return "当前为 IPv6 投屏，请开启反控手机并重新连接投屏。";
        if (port < 1 || port > 65535 || String.IsNullOrEmpty(password)) return "请检查 SSH 端口和密码。";
        var start = new ProcessStartInfo {
            FileName = Path.Combine(Environment.SystemDirectory, @"OpenSSH\ssh.exe"),
            Arguments = "-F none -4 -T -o ConnectTimeout=5 -o ConnectionAttempts=1 -o NumberOfPasswordPrompts=1 " +
                "-o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o HostKeyAlgorithms=+ssh-rsa " +
                "-p " + port + " root@" + address + " exit 0",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.EnvironmentVariables["SSH_ASKPASS"] = System.Windows.Forms.Application.ExecutablePath;
        start.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
        start.EnvironmentVariables["DISPLAY"] = "iscreenx";
        start.EnvironmentVariables[PasswordVariable] = password;
        try {
            using (var process = Process.Start(start)) {
                process.StandardInput.Close();
                var errors = process.StandardError.ReadToEndAsync();
                process.BeginOutputReadLine();
                if (!process.WaitForExit(10000)) {
                    process.Kill();
                    return "SSH 连接超时，请检查手机端口和网络。";
                }
                if (process.ExitCode == 0) return LoginSuccess;
                string error = errors.Result.ToLowerInvariant();
                if (error.Contains("host key verification failed") || error.Contains("host identification has changed"))
                    return "SSH 主机密钥验证失败，请核对手机身份。";
                if (error.Contains("permission denied")) return "SSH 登录失败，请检查root密码和登录权限。";
                if (error.Contains("connection refused")) return "手机拒绝连接，请检查 SSH 服务和端口。";
                if (error.Contains("no matching")) return "手机的 SSH 算法与 Windows 客户端不兼容。";
                return "SSH 连接失败，请检查手机网络、端口和 SSH 服务。";
            }
        } catch (Win32Exception) { return "无法运行 Windows OpenSSH 客户端，请检查其安装状态。"; }
    }
}
