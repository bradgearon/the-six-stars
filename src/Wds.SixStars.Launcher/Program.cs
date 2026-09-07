using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wds.SixStars.Launcher;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = LauncherOptions.Parse(args);
        var root = FindRepositoryOrAppRoot(AppContext.BaseDirectory);
        Directory.SetCurrentDirectory(root);

        Console.WriteLine("======================================================================");
        Console.WriteLine("Six Stars Native AI Companion Launcher");
        Console.WriteLine("======================================================================");
        Console.WriteLine("Root: " + root);
        var mode = options.DownloadModelsOnly
            ? "download-models-only"
            : options.InstallOnly ? "install-only" : "install + launch";
        Console.WriteLine("Mode: " + mode);
        Console.WriteLine();

        // Runtime packs and model weights are deliberately separate. The PowerShell installer
        // only stages/validates the native executables; the launcher itself owns resilient model
        // downloads through the bezzad/Downloader library and config/models.json.
        if (!options.SkipInstall && !options.DownloadModelsOnly)
        {
            var installExit = RunInstaller(root, options.Profile, options.ForceInstall);
            if (installExit != 0)
            {
                Console.WriteLine("Installer exited with code " + installExit + ". Fix the error above or rerun with --skip-install after manual setup.");
                return installExit;
            }
        }

        var manifestPath = FullPath(root, options.ManifestPath ?? Path.Combine("config", "runtime-manifest.json"));
        var modelsPath = FullPath(root, options.ModelsPath ?? Path.Combine("config", "models.json"));
        var manifest = RuntimeManifest.Load(manifestPath);
        var models = ModelManifest.Load(modelsPath);

        if (!options.SkipModelDownload)
        {
            await ModelDownloadManager.EnsureModelsAsync(root, models);
        }

        if (options.DownloadModelsOnly)
        {
            Console.WriteLine("Model-download-only requested. Model files are ready.");
            return 0;
        }

        if (options.InstallOnly)
        {
            Console.WriteLine("Install-only requested. Runtime setup and model downloads are complete.");
            return 0;
        }

        var hardware = HardwareDetector.Detect();

        Console.WriteLine($"Hardware: NVIDIA={hardware.HasNvidia}; VulkanProbe={hardware.HasVulkan}; OS={hardware.OsDescription}");
        Console.WriteLine("Manifest: " + manifestPath);
        Console.WriteLine("Models:   " + modelsPath);
        Console.WriteLine();

        if (manifest.GpuPolicy.RequireCuda && !hardware.HasNvidia)
        {
            Console.WriteLine(manifest.GpuPolicy.HostedServiceMessage ?? "An NVIDIA GPU/driver is required by this runtime profile.");
            return 3;
        }

        using var supervisor = new RuntimeSupervisor(root, manifest, models, hardware);
        await supervisor.StartAllAsync(options.WaitSeconds);

        if (options.NoUi)
        {
            Console.WriteLine("--no-ui was passed. Native AI services are running. Press Enter to stop.");
            Console.ReadLine();
            return 0;
        }

        var uiExe = ResolveUiExe(root, options.UiPath);
        if (uiExe == null)
        {
            Console.WriteLine("Could not find the Six Stars WPF UI executable. Build/publish it first.");
            Console.WriteLine("Servers are running. Press Enter to stop.");
            Console.ReadLine();
            return 2;
        }

        Console.WriteLine("Launching UI: " + uiExe);
        using var ui = StartUi(uiExe, root, supervisor);
        Console.WriteLine("UI started. Close the UI window to stop llama-server, whisper.cpp, and qwentts.cpp.");
        ui.WaitForExit();
        Console.WriteLine("UI exited with code " + ui.ExitCode + ". Stopping local services.");
        return ui.ExitCode;
    }

    private static int RunInstaller(string root, string profile, bool force)
    {
        var script = Path.Combine(root, "scripts", "Install-AiRuntime.ps1");
        if (!File.Exists(script))
        {
            Console.WriteLine("Installer script not found, skipping: " + script);
            return 0;
        }

        // Model downloads are handled by the .NET launcher using bezzad/Downloader. Passing
        // -SkipModels prevents the legacy PowerShell downloader from doing the same work first.
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Profile {profile} -SkipModels";
        if (force) args += " -Force";
        Console.WriteLine("Running native runtime installer/validator...");
        Console.WriteLine("powershell " + args);

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = args,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        });

        if (proc == null) return 1;
        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static string FindRepositoryOrAppRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SixStars.sln")) || File.Exists(Path.Combine(dir.FullName, "config", "runtime-manifest.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Path.GetFullPath(start);
    }

    private static string? ResolveUiExe(string root, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var full = FullPath(root, overridePath);
            return File.Exists(full) ? full : null;
        }

        string[] candidates =
        {
            Path.Combine(root, "six-stars.exe"),
            Path.Combine(root, "ui", "six-stars.exe"),
            Path.Combine(root, "src", "Wds.SixStars.Ui", "bin", "Release", "net8.0-windows", "win-x64", "publish", "six-stars.exe"),
            Path.Combine(root, "src", "Wds.SixStars.Ui", "bin", "Release", "net8.0-windows", "six-stars.exe"),
            Path.Combine(root, "src", "Wds.SixStars.Ui", "bin", "Debug", "net8.0-windows", "six-stars.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static Process StartUi(string uiExe, string root, RuntimeSupervisor supervisor)
    {
        var psi = new ProcessStartInfo
        {
            FileName = uiExe,
            WorkingDirectory = Path.GetDirectoryName(uiExe) ?? root,
            UseShellExecute = false
        };

        ApplySharedEnvironment(psi.Environment, root, supervisor.ModelManifest);

        psi.Environment["SIX_STARS_LLM_ENDPOINT"] = supervisor.OpenAiChatUrl ?? "http://127.0.0.1:8080/v1/chat/completions";
        psi.Environment["SIX_STARS_SCREEN_LLM_ENDPOINT"] = supervisor.OpenAiChatUrl ?? "http://127.0.0.1:8080/v1/chat/completions";
        psi.Environment["SIX_STARS_LLM_MODEL"] = supervisor.ModelManifest.Models.Llm?.OpenAiModelName
            ?? supervisor.ModelManifest.Models.Llm?.ModelName
            ?? "six-stars-local";
        psi.Environment["SIX_STARS_SCREEN_LLM_MODEL"] = supervisor.ModelManifest.Models.Vision?.OpenAiModelName
            ?? supervisor.ModelManifest.Models.Llm?.OpenAiModelName
            ?? supervisor.ModelManifest.Models.Llm?.ModelName
            ?? "six-stars-local";
        psi.Environment["SIX_STARS_SCREEN_LLM_ENABLED"] = supervisor.VisionReady ? "true" : "false";
        psi.Environment["SIX_STARS_SCREEN_LLM_USE_JSON_SCHEMA"] = "false";
        psi.Environment["SIX_STARS_LLM_USE_JSON_SCHEMA"] = "false";
        psi.Environment["SIX_STARS_LLM_MAX_TOKENS"] = "0";
        psi.Environment["SIX_STARS_LLM_SPEECH_MAX_TOKENS"] = "0";
        psi.Environment["SIX_STARS_LLM_REVIEW_MAX_TOKENS"] = "0";
        psi.Environment["SIX_STARS_LLM_STORY_PLAN_MAX_TOKENS"] = "0";
        psi.Environment["SIX_STARS_LLM_DISABLE_REASONING"] = "true";
        psi.Environment["SIX_STARS_LLM_REASONING_BUDGET"] = "0";
        psi.Environment["SIX_STARS_LLM_REASONING_FORMAT"] = "none";
        psi.Environment["SIX_STARS_LLM_SEND_REASONING_CONTROL_FIELDS"] = "true";
        psi.Environment["SIX_STARS_SCREEN_LLM_DISABLE_REASONING"] = "true";
        psi.Environment["SIX_STARS_SCREEN_LLM_REASONING_BUDGET"] = "0";
        psi.Environment["SIX_STARS_SCREEN_LLM_REASONING_FORMAT"] = "none";
        psi.Environment["SIX_STARS_SCREEN_LLM_SEND_REASONING_CONTROL_FIELDS"] = "true";

        psi.Environment["SIX_STARS_WHISPER_PROVIDER"] = "whispercpp";
        psi.Environment["SIX_STARS_WHISPER_ENDPOINT"] = supervisor.AsrUrl ?? "http://127.0.0.1:8000/inference";
        psi.Environment["SIX_STARS_QWENTTS_HTTP_URL"] = supervisor.TtsHttpUrl ?? "http://127.0.0.1:8101";
        psi.Environment["SIX_STARS_QWENTTS_MODEL"] = supervisor.ModelManifest.Models.Tts?.Alias
            ?? supervisor.ModelManifest.Models.Tts?.ModelName
            ?? "qwen3-tts-base";
        psi.Environment["SIX_STARS_USE_STREAMING_TTS"] = "true";
        psi.Environment["SIX_STARS_AUDIO_KEEP_ALIVE"] = psi.Environment.TryGetValue("SIX_STARS_AUDIO_KEEP_ALIVE", out var keepAlive) ? keepAlive : "true";
        psi.Environment["SIX_STARS_AUDIO_KEEPALIVE_BACKEND"] = psi.Environment.TryGetValue("SIX_STARS_AUDIO_KEEPALIVE_BACKEND", out var keepAliveBackend) ? keepAliveBackend : "directsound";
        psi.Environment["SIX_STARS_REQUIRE_WAKE_WORD"] = psi.Environment.TryGetValue("SIX_STARS_REQUIRE_WAKE_WORD", out var wake) ? wake : "false";

        return Process.Start(psi) ?? throw new InvalidOperationException("Could not launch UI: " + uiExe);
    }

    internal static void ApplySharedEnvironment(IDictionary<string, string?> env, string root, ModelManifest models)
    {
        var modelsRoot = Path.Combine(root, "models");
        env["SIX_STARS_APP_ROOT"] = root;
        env["SIX_STARS_REPO_ROOT"] = root;
        env["SIX_STARS_MODELS_ROOT"] = modelsRoot;
        env["SIX_STARS_REQUIRE_CUDA"] = "true";

        env["SIX_STARS_WHISPER_PROVIDER"] = "whispercpp";
        env["SIX_STARS_WHISPER_ENDPOINT"] = "http://127.0.0.1:8000/inference";
        env["SIX_STARS_WHISPER_MODEL_PATH"] = models.Models.Asr?.LocalPath is { Length: > 0 } asrPath ? FullPath(root, asrPath) : string.Empty;

        env["SIX_STARS_QWENTTS_HTTP_URL"] = "http://127.0.0.1:8101";
        env["SIX_STARS_USE_STREAMING_TTS"] = "true";
        env["SIX_STARS_QWENTTS_MODEL"] = models.Models.Tts?.Alias ?? models.Models.Tts?.ModelName ?? "qwen3-tts-base";
    }

    internal static string FullPath(string root, string path) => Path.IsPathRooted(path)
        ? path
        : Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
}

internal sealed record LauncherOptions(
    string? ManifestPath,
    string? ModelsPath,
    string? UiPath,
    string Profile,
    bool SkipInstall,
    bool SkipModelDownload,
    bool ForceInstall,
    bool InstallOnly,
    bool DownloadModelsOnly,
    bool NoUi,
    int WaitSeconds)
{
    public static LauncherOptions Parse(string[] args)
    {
        string? manifest = null;
        string? models = null;
        string? ui = null;
        string profile = "auto";
        bool skipInstall = false;
        bool skipModelDownload = false;
        bool forceInstall = false;
        bool installOnly = false;
        bool downloadModelsOnly = false;
        bool noUi = false;
        int waitSeconds = 120;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest": manifest = args[++i]; break;
                case "--models": models = args[++i]; break;
                case "--ui": ui = args[++i]; break;
                case "--profile": profile = args[++i]; break;
                case "--skip-install": skipInstall = true; break;
                case "--skip-model-download": skipModelDownload = true; break;
                case "--force-install": forceInstall = true; break;
                case "--install-only": installOnly = true; break;
                case "--download-models-only": downloadModelsOnly = true; break;
                case "--no-ui": noUi = true; break;
                case "--wait-seconds": waitSeconds = int.Parse(args[++i]); break;
            }
        }

        return new LauncherOptions(
            manifest,
            models,
            ui,
            profile,
            skipInstall,
            skipModelDownload,
            forceInstall,
            installOnly,
            downloadModelsOnly,
            noUi,
            waitSeconds);
    }
}

internal sealed class RuntimeSupervisor : IDisposable
{
    private readonly string _root;
    private readonly RuntimeManifest _manifest;
    private readonly HardwareInfo _hardware;
    private readonly List<ManagedProcess> _processes = new();

    public RuntimeSupervisor(string root, RuntimeManifest manifest, ModelManifest models, HardwareInfo hardware)
    {
        _root = root;
        _manifest = manifest;
        ModelManifest = models;
        _hardware = hardware;
    }

    public ModelManifest ModelManifest { get; }
    public string? OpenAiChatUrl { get; private set; }
    public string? AsrUrl { get; private set; }
    public string? TtsHttpUrl { get; private set; }
    public bool VisionReady { get; private set; }
    public string LlmRuntime { get; private set; } = string.Empty;

    public async Task StartAllAsync(int waitSeconds)
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs", "services"));

        // Keep qwentts.cpp and whisper.cpp hot for conversational latency. The .NET application
        // talks directly to both native HTTP services; there is no separate gateway process.
        await StartKindAsync("tts", waitSeconds);
        await StartKindAsync("asr", Math.Min(waitSeconds, 60));
        await StartKindAsync("llm", waitSeconds);
    }

    private async Task StartKindAsync(string kind, int waitSeconds)
    {
        var order = _manifest.SelectionOrder.TryGetValue(kind, out var ids)
            ? ids
            : _manifest.Packs.Where(p => p.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToList();

        foreach (var id in order)
        {
            var pack = _manifest.Packs.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (pack == null || !pack.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsCompatible(pack)) continue;

            ManagedProcess? proc = null;
            try
            {
                proc = StartPack(pack);
                if (!await WaitForReadyAsync(pack, proc, TimeSpan.FromSeconds(waitSeconds)))
                {
                    Console.WriteLine($"Readiness check failed for {pack.Id}; trying next fallback.");
                    Kill(proc);
                    proc = null;
                    continue;
                }

                _processes.Add(proc!);
                proc = null; // supervisor now owns the process
                switch (kind.ToLowerInvariant())
                {
                    case "llm":
                        LlmRuntime = pack.Runtime;
                        OpenAiChatUrl = pack.OpenAiChatUrl ?? "http://127.0.0.1:8080/v1/chat/completions";
                        UpdateVisionReadyForStartedLlm(pack);
                        break;
                    case "asr":
                        AsrUrl = pack.AsrUrl ?? "http://127.0.0.1:8000/inference";
                        break;
                    case "tts":
                        TtsHttpUrl = pack.TtsHttpUrl ?? "http://127.0.0.1:8101";
                        break;
                }

                Console.WriteLine($"READY   {kind}: {pack.Id}");
                return;
            }
            catch (Exception ex)
            {
                if (proc != null) Kill(proc);
                Console.WriteLine($"SKIP    {pack.Id}: {ex.Message}");
            }
        }

        var message = $"No {kind} native service started. "
            + (_manifest.GpuPolicy.HostedServiceMessage ?? "Install/configure the native NVIDIA runtime pack or use a hosted service option.");
        throw new InvalidOperationException(message);
    }

    private bool IsCompatible(RuntimePack pack)
    {
        var vendor = pack.Vendor.ToLowerInvariant();
        if (_manifest.GpuPolicy.RequireCuda && !_manifest.GpuPolicy.AllowCpuFallback)
        {
            return vendor == "nvidia" && _hardware.HasNvidia;
        }

        return vendor switch
        {
            "nvidia" => _hardware.HasNvidia,
            "generic-gpu" => _hardware.HasVulkan || _hardware.HasNvidia,
            "cpu" => _manifest.GpuPolicy.AllowCpuFallback,
            _ => true
        };
    }

    private ManagedProcess StartPack(RuntimePack pack)
    {
        var exe = Program.FullPath(_root, pack.Path);
        if (!File.Exists(exe)) throw new FileNotFoundException("Runtime pack executable is missing", exe);
        var cwd = Program.FullPath(_root, pack.WorkingDirectory ?? ".");
        Directory.CreateDirectory(cwd);

        var args = ResolveArgs(pack);
        var logPath = Path.Combine(_root, "logs", "services", pack.Id + ".log");
        Console.WriteLine("START   " + pack.Id);
        Console.WriteLine("        " + exe + " " + args);
        Console.WriteLine("        log: " + logPath);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Program.ApplySharedEnvironment(psi.Environment, _root, ModelManifest);
        ApplyPackEnvironment(pack, psi.Environment);

        var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + pack.Id);
        var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        log.WriteLine();
        log.WriteLine("================================================================================");
        log.WriteLine(DateTimeOffset.Now.ToString("O") + " starting " + pack.Id);
        log.WriteLine(exe + " " + args);
        log.WriteLine("================================================================================");
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new ManagedProcess(pack.Id, p, log);
    }

    private void ApplyPackEnvironment(RuntimePack pack, IDictionary<string, string?> env)
    {
        foreach (var item in pack.Environment)
        {
            env[item.Key] = ExpandTokens(item.Value);
        }

        if (pack.Vendor.Equals("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            env["SIX_STARS_REQUIRE_CUDA"] = "true";
        }
    }

    private string ResolveArgs(RuntimePack pack)
    {
        var args = ExpandTokens(pack.Args ?? string.Empty);
        var runtime = pack.Runtime.ToLowerInvariant();

        if (runtime == "whispercpp")
        {
            var asr = ModelManifest.Models.Asr ?? throw new InvalidOperationException("models.asr is missing from config/models.json");
            var modelPath = RequireModelFile(asr.LocalPath, "whisper.cpp ASR model");
            if (!ContainsArgument(args, "-m") && !ContainsArgument(args, "--model"))
            {
                args = "-m " + Quote(modelPath) + " " + args;
            }
        }
        else if (runtime == "qwenttscpp")
        {
            var tts = ModelManifest.Models.Tts ?? throw new InvalidOperationException("models.tts is missing from config/models.json");
            var talker = RequireModelFile(tts.LocalPath, "qwentts.cpp talker GGUF");
            var codec = RequireModelFile(tts.CodecLocalPath, "qwentts.cpp codec/tokenizer GGUF");
            if (!ContainsArgument(args, "--model")) args = "--model " + Quote(talker) + " " + args;
            if (!ContainsArgument(args, "--codec")) args = "--codec " + Quote(codec) + " " + args;
            if (!ContainsArgument(args, "--alias") && !string.IsNullOrWhiteSpace(tts.Alias)) args += " --alias " + Quote(tts.Alias);
        }
        else if (runtime == "llamacpp")
        {
            var llm = ModelManifest.Models.Llm;
            if (llm != null && !string.IsNullOrWhiteSpace(llm.LocalPath) && File.Exists(Program.FullPath(_root, llm.LocalPath)))
            {
                args = "-m " + Quote(Program.FullPath(_root, llm.LocalPath)) + " " + args;
            }
            else if (llm != null && !string.IsNullOrWhiteSpace(llm.HfSpecifier))
            {
                args = "-hf " + Quote(llm.HfSpecifier) + " " + args;
            }

            args = AppendVisionProjectorArgs(args);
            args = AppendReasoningDisabledArgs(args);
        }

        return args.Trim();
    }

    private string RequireModelFile(string? relativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidOperationException(description + " path is not configured.");
        var full = Program.FullPath(_root, relativePath);
        if (!File.Exists(full)) throw new FileNotFoundException(description + " is missing", full);
        return full;
    }

    private string ExpandTokens(string value)
    {
        return value
            .Replace("{root}", _root, StringComparison.OrdinalIgnoreCase)
            .Replace("{models}", Path.Combine(_root, "models"), StringComparison.OrdinalIgnoreCase)
            .Replace("{engines}", Path.Combine(_root, "engines"), StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendReasoningDisabledArgs(string args)
    {
        if (!ContainsArgument(args, "--reasoning")) args += " --reasoning off";
        if (!ContainsArgument(args, "--reasoning-budget")) args += " --reasoning-budget 0";
        if (!ContainsArgument(args, "--reasoning-format")) args += " --reasoning-format none";
        return args.Trim();
    }

    private static bool ContainsArgument(string args, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(args)) return false;
        return args.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(x => x.Equals(argumentName, StringComparison.OrdinalIgnoreCase));
    }

    private string AppendVisionProjectorArgs(string args)
    {
        if (args.Contains("--mmproj", StringComparison.OrdinalIgnoreCase))
        {
            VisionReady = true;
            return args;
        }

        var vision = ModelManifest.Models.Vision;
        if (vision == null || !vision.Enabled)
        {
            VisionReady = false;
            return args;
        }

        if (string.IsNullOrWhiteSpace(vision.LocalPath))
        {
            VisionReady = false;
            return args;
        }

        var mmprojPath = Program.FullPath(_root, vision.LocalPath);
        if (!File.Exists(mmprojPath))
        {
            Console.WriteLine("VISION mmproj not found; screen vision disabled for this launch: " + mmprojPath);
            VisionReady = false;
            return args;
        }

        VisionReady = true;
        return (args + " --mmproj " + Quote(mmprojPath)).Trim();
    }

    private void UpdateVisionReadyForStartedLlm(RuntimePack pack)
    {
        if (!pack.SupportsVision)
        {
            VisionReady = false;
            return;
        }

        var vision = ModelManifest.Models.Vision;
        VisionReady = vision?.Enabled == true
            && !string.IsNullOrWhiteSpace(vision.LocalPath)
            && File.Exists(Program.FullPath(_root, vision.LocalPath));
    }

    private static string Quote(string value) => value.Contains(' ') || value.Contains('\t') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static async Task<bool> WaitForReadyAsync(RuntimePack pack, ManagedProcess process, TimeSpan timeout)
    {
        if (!string.IsNullOrWhiteSpace(pack.HealthUrl) && pack.HealthUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return await WaitForHttpHealthAsync(pack.HealthUrl, timeout, process.Process);
        }

        if (pack.HealthTcpPort is > 0)
        {
            return await WaitForTcpPortAsync(pack.HealthTcpHost ?? "127.0.0.1", pack.HealthTcpPort.Value, timeout, process.Process);
        }

        await Task.Delay(1500);
        return !process.Process.HasExited;
    }

    private static async Task<bool> WaitForHttpHealthAsync(string url, TimeSpan timeout, Process process)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            if (process.HasExited) return false;
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode) return true;
            }
            catch { }
            await Task.Delay(750);
        }
        return false;
    }

    private static async Task<bool> WaitForTcpPortAsync(string host, int port, TimeSpan timeout, Process process)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            if (process.HasExited) return false;
            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                await tcp.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch { }
            await Task.Delay(500);
        }
        return false;
    }

    private static void Kill(ManagedProcess p)
    {
        try { if (!p.Process.HasExited) p.Process.Kill(entireProcessTree: true); } catch { }
        p.Dispose();
    }

    public void Dispose()
    {
        for (int i = _processes.Count - 1; i >= 0; i--)
        {
            Kill(_processes[i]);
        }
    }
}


internal sealed class ManagedProcess : IDisposable
{
    public ManagedProcess(string id, Process process, StreamWriter log)
    {
        Id = id;
        Process = process;
        Log = log;
    }

    public string Id { get; }
    public Process Process { get; }
    public StreamWriter Log { get; }

    public void Dispose()
    {
        try { Log.Dispose(); } catch { }
        try { Process.Dispose(); } catch { }
    }
}

internal static class HardwareDetector
{
    public static HardwareInfo Detect()
    {
        return new HardwareInfo(
            HasNvidia: CommandWorks("nvidia-smi", "--query-gpu=name --format=csv,noheader"),
            HasVulkan: CommandWorks("vulkaninfo", "--summary"),
            OsDescription: RuntimeInformation.OSDescription);
    }

    private static bool CommandWorks(string fileName, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            return p != null && p.WaitForExit(2500) && p.ExitCode == 0;
        }
        catch { return false; }
    }
}

internal sealed record HardwareInfo(bool HasNvidia, bool HasVulkan, string OsDescription);

internal sealed class RuntimeManifest
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("gpuPolicy")] public RuntimeGpuPolicy GpuPolicy { get; set; } = new();
    [JsonPropertyName("selectionOrder")] public Dictionary<string, List<string>> SelectionOrder { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("packs")] public List<RuntimePack> Packs { get; set; } = new();
    public static RuntimeManifest Load(string path) => JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(path), JsonOptions()) ?? new RuntimeManifest();
    internal static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}

internal sealed class RuntimeGpuPolicy
{
    [JsonPropertyName("requireCuda")] public bool RequireCuda { get; set; } = true;
    [JsonPropertyName("allowCpuFallback")] public bool AllowCpuFallback { get; set; } = false;
    [JsonPropertyName("hostedServiceMessage")] public string? HostedServiceMessage { get; set; } = "A compatible NVIDIA driver/GPU is required for the bundled native CUDA runtime. The full CUDA Toolkit is not required when the redistributed runtime pack includes its CUDA runtime DLLs.";
}

internal sealed class RuntimePack
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("runtime")] public string Runtime { get; set; } = string.Empty;
    [JsonPropertyName("vendor")] public string Vendor { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("workingDirectory")] public string? WorkingDirectory { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
    [JsonPropertyName("environment")] public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("healthUrl")] public string? HealthUrl { get; set; }
    [JsonPropertyName("healthTcpHost")] public string? HealthTcpHost { get; set; }
    [JsonPropertyName("healthTcpPort")] public int? HealthTcpPort { get; set; }
    [JsonPropertyName("openAiChatUrl")] public string? OpenAiChatUrl { get; set; }
    [JsonPropertyName("asrUrl")] public string? AsrUrl { get; set; }
    [JsonPropertyName("ttsHttpUrl")] public string? TtsHttpUrl { get; set; }
    [JsonPropertyName("supportsVision")] public bool SupportsVision { get; set; }
}

internal sealed class ModelManifest
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("models")] public ModelSet Models { get; set; } = new();
    public static ModelManifest Load(string path) => JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(path), RuntimeManifest.JsonOptions()) ?? new ModelManifest();
}

internal sealed class ModelSet
{
    [JsonPropertyName("llm")] public ModelEntry? Llm { get; set; }
    [JsonPropertyName("vision")] public ModelEntry? Vision { get; set; }
    [JsonPropertyName("asr")] public ModelEntry? Asr { get; set; }
    [JsonPropertyName("tts")] public ModelEntry? Tts { get; set; }
}

internal sealed class ModelEntry
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("preferredMode")] public string? PreferredMode { get; set; }
    [JsonPropertyName("hfSpecifier")] public string? HfSpecifier { get; set; }
    [JsonPropertyName("localPath")] public string? LocalPath { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("downloadUrls")] public List<string> DownloadUrls { get; set; } = new();
    [JsonPropertyName("codecLocalPath")] public string? CodecLocalPath { get; set; }
    [JsonPropertyName("codecDownloadUrl")] public string? CodecDownloadUrl { get; set; }
    [JsonPropertyName("codecDownloadUrls")] public List<string> CodecDownloadUrls { get; set; } = new();
    [JsonPropertyName("modelName")] public string? ModelName { get; set; }
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("openAiModelName")] public string? OpenAiModelName { get; set; }
    [JsonPropertyName("contextSize")] public int ContextSize { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}
