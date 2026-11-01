using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AutoPrinterSetup;

public sealed record PsResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
    public string Message => (Error.Length > 0 ? Error : Output).Trim();
}

public static class PowerShellRunner
{
    const string Prelude = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        """;

    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    public static Task<PsResult> RunAsync(string script, bool elevated = false) =>
        elevated ? RunElevatedAsync(script) : RunNormalAsync(script);

    static async Task<PsResult> RunNormalAsync(string script)
    {
        string wrapped = Prelude + "try {\n" + script + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var psi = new ProcessStartInfo("powershell.exe", Arguments(wrapped))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new PsResult(process.ExitCode, await stdout, await stderr);
    }

    static async Task<PsResult> RunElevatedAsync(string script)
    {
        // An elevated process can't share our console streams, so its output goes through a temp file.
        string outFile = Path.Combine(Path.GetTempPath(), $"auto-printer-setup-{Guid.NewGuid():N}.txt");
        string target = Quote(outFile);
        string wrapped = Prelude +
            "try {\n& {\n" + script + "\n} *>&1 | Out-File -FilePath " + target + " -Encoding utf8\n}" +
            " catch { $_.Exception.Message | Out-File -FilePath " + target + " -Append -Encoding utf8; exit 1 }";
        var psi = new ProcessStartInfo("powershell.exe", Arguments(wrapped))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
            string text = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile) : "";
            return process.ExitCode == 0 ? new PsResult(0, text, "") : new PsResult(process.ExitCode, "", text);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new PsResult(1223, "", "Administrator permission was not given.");
        }
        finally
        {
            try { File.Delete(outFile); } catch { }
        }
    }

    static string Arguments(string script) =>
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
