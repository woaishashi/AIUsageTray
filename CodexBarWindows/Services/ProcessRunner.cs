using System.ComponentModel;
using System.Diagnostics;

namespace CodexBarWindows.Services;

internal sealed class ProcessRunner
{
    public async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                return CommandResult.Failed("command did not start");
            }
        }
        catch (Win32Exception ex)
        {
            return CommandResult.Failed(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.Failed(ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return CommandResult.Failed("command timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return process.ExitCode == 0
            ? CommandResult.Success(stdout, stderr)
            : CommandResult.Failed(FirstNonEmpty(stderr, stdout, $"command exited with {process.ExitCode}"));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup after a bounded probe timeout.
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}

internal sealed record CommandResult(
    bool Succeeded,
    string StandardOutput,
    string StandardError,
    string? ErrorMessage)
{
    public static CommandResult Success(string standardOutput, string standardError)
    {
        return new CommandResult(true, standardOutput, standardError, null);
    }

    public static CommandResult Failed(string errorMessage)
    {
        return new CommandResult(false, string.Empty, string.Empty, SecretSafeText.ForDisplay(errorMessage));
    }
}
