using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Ui.Configuration;

namespace Wds.SixStars.Ui.Services;

internal sealed class DesktopDiagnosticsService
{
    internal sealed record ActionResult(bool Ok, string Message, string Text = "", string Path = "");

    private readonly SixStarsRuntime _runtime;
    private readonly AppConfiguration _configuration;

    public DesktopDiagnosticsService(SixStarsRuntime runtime, AppConfiguration configuration)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public ActionResult LoadLatestConversationLog()
    {
        string text = _runtime.ReadLatestConversationDebugLogText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ActionResult(false,
                "No latest conversation emotion trace file yet. Log folder: " + _runtime.ConversationDebugLogDirectory);
        }
        return new ActionResult(true, "Loaded latest conversation emotion trace: " + _runtime.LatestConversationDebugLogPath,
            text, _runtime.LatestConversationDebugLogPath);
    }

    public ActionResult CopyLatestConversationLog(string fallback)
    {
        string text = _runtime.ReadLatestConversationDebugLogText();
        if (string.IsNullOrWhiteSpace(text)) text = fallback ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return new ActionResult(false, "No conversation emotion trace text to copy yet.");
        Clipboard.SetText(text);
        return new ActionResult(true, "Copied latest conversation emotion trace to clipboard.", text);
    }

    public ActionResult OpenLatestConversationLog()
    {
        string path = _runtime.LatestConversationDebugLogPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new ActionResult(false, "No latest conversation emotion trace file exists yet. Log folder: " + _runtime.ConversationDebugLogDirectory);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = "\"" + path + "\"", UseShellExecute = true });
            return new ActionResult(true, "Opened latest conversation emotion trace: " + path, Path: path);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Could not open latest conversation emotion trace: " + ex.Message, Path: path);
        }
    }

    public string ResolveLlmExchangeLogDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("SIX_STARS_LLM_LOG_DIR")
            ?? _configuration.LLM.ExchangeLogDirectory;
        if (string.IsNullOrWhiteSpace(configured)) configured = "logs/llm-exchanges";
        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }

    public ActionResult OpenLlmExchangeLogFolder()
    {
        try
        {
            string folder = ResolveLlmExchangeLogDirectory();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            return new ActionResult(true, "Opened LLM exchange log folder: " + folder, Path: folder);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Could not open LLM exchange log folder: " + ex.Message);
        }
    }
}
