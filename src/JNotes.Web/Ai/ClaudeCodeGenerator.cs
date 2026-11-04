using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using JNotes.Core;

namespace JNotes.Web.Ai;

public sealed class ClaudeCodeOptions
{
    /// <summary>Claude Code executable (on PATH, or a full path).</summary>
    public string Command { get; set; } = "claude";
    /// <summary>Optional model alias, e.g. "sonnet" or "opus". Empty = Claude Code's default for your plan.</summary>
    public string? Model { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
}

/// <summary>
/// Generates answers by running Claude Code headless (<c>claude -p</c>) with the user's Claude subscription
/// (their normal login on a PC, or CLAUDE_CODE_OAUTH_TOKEN from <c>claude setup-token</c> in Docker).
/// It runs with no tools, no MCP servers, no user/project settings and no saved session: text in, markdown out.
/// </summary>
public sealed class ClaudeCodeGenerator(ClaudeCodeOptions options, ILogger<ClaudeCodeGenerator> log) : IAiGenerator
{
    private readonly string _workDir = Directory.CreateTempSubdirectory("jnotes-claude-").FullName;

    public async Task<string> GenerateAsync(GenerationContext context, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(options.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _workDir, // empty dir: no CLAUDE.md or project settings get picked up
        };
        foreach (var arg in new[]
                 {
                     "-p", "--output-format", "json",
                     "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--no-session-persistence",
                     "--system-prompt", AiPrompt.System,
                 })
            psi.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(options.Model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(options.Model); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new GenerationException("Couldn't start Claude Code.");
        }
        catch (Win32Exception ex)
        {
            throw new GenerationException($"Claude Code isn't installed or not found (“{options.Command}”). See the Claude page for setup.", ex);
        }

        using (process)
        {
            try
            {
                await process.StandardInput.WriteAsync(AiPrompt.User(context).AsMemory(), timeout.Token);
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return Parse(await stdout, await stderr, process.ExitCode);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                throw new GenerationException($"Claude took longer than {options.TimeoutSeconds}s and was stopped.");
            }
            catch
            {
                TryKill(process);
                throw;
            }
        }
    }

    private string Parse(string stdout, string stderr, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var text = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var isError = root.TryGetProperty("is_error", out var e) && e.GetBoolean();
            if (isError || exitCode != 0)
            {
                log.LogWarning("Claude Code failed (exit {Exit}): {Result} {Stderr}", exitCode, text, stderr);
                throw new GenerationException(FriendlyError(text + " " + stderr));
            }
            return text;
        }
        catch (JsonException)
        {
            log.LogWarning("Claude Code returned non-JSON output (exit {Exit}): {Stdout} {Stderr}", exitCode, stdout, stderr);
            throw new GenerationException(FriendlyError(stdout + " " + stderr));
        }
    }

    private static string FriendlyError(string raw)
    {
        var s = raw.ToLowerInvariant();
        if (s.Contains("login") || s.Contains("log in") || s.Contains("auth") || s.Contains("oauth") || s.Contains("401"))
            return "Claude Code isn't signed in. On the server, set CLAUDE_CODE_OAUTH_TOKEN (from `claude setup-token`).";
        if (s.Contains("limit") || s.Contains("429") || s.Contains("quota"))
            return "You've hit your Claude usage limit; try again later.";
        return "Claude couldn't answer this request. Check the server log for details.";
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
