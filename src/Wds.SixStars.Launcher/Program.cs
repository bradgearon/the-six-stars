using System.Diagnostics;
using System.Net.Http.Json;
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
        Console.WriteLine("Six Stars AI Companion Launcher");
        Console.WriteLine("======================================================================");
        Console.WriteLine("Root: " + root);
        Console.WriteLine("Mode: " + (options.InstallOnly ? "install-only" : "install + launch"));
        Console.WriteLine();

        if (!options.SkipInstall)
        {
            var installExit = RunInstaller(root, options.Profile, options.ForceInstall);
            if (installExit != 0)
            {
                Console.WriteLine("Installer exited with code " + installExit + ". Fix the error above or rerun with --skip-install after manual setup.");
                return installExit;
            }
        }

        if (options.InstallOnly)
        {
            Console.WriteLine("Install-only requested. Runtime setup complete.");
            return 0;
        }

        var manifestPath = FullPath(root, options.ManifestPath ?? Path.Combine("config", "runtime-manifest.json"));
        var modelsPath = FullPath(root, options.ModelsPath ?? Path.Combine("config", "models.json"));
        var manifest = RuntimeManifest.Load(manifestPath);
        var models = ModelManifest.Load(modelsPath);
        var hardware = HardwareDetector.Detect();
        Console.WriteLine($"Hardware: NVIDIA={hardware.HasNvidia}; VulkanProbe={hardware.HasVulkan}; OS={hardware.OsDescription}");
        Console.WriteLine("Manifest: " + manifestPath);
        Console.WriteLine("Models:   " + modelsPath);
        Console.WriteLine();

        using var supervisor = new RuntimeSupervisor(root, manifest, models, hardware);
        await supervisor.StartAllAsync(options.WaitSeconds);

        if (options.NoUi)
        {
            Console.WriteLine("--no-ui was passed. Servers are running. Press Enter to stop.");
            Console.ReadLine();
            return 0;
        }

        var uiExe = ResolveUiExe(root, options.UiPath);
        if (uiExe == null)
        {
            Console.WriteLine("Could not find the Six Stars WPF UI executable. Build it first with scripts\\Build-AiRuntimeRedist.ps1 or run dotnet publish.");
            Console.WriteLine("Servers are running. Press Enter to stop.");
            Console.ReadLine();
            return 2;
        }

        Console.WriteLine("Launching UI: " + uiExe);
        using var ui = StartUi(uiExe, root, supervisor);
        Console.WriteLine("UI started. Close the UI window to stop local AI servers.");
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

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Profile {profile}";
        if (force) args += " -Force";
        Console.WriteLine("Running installer script...");
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
        psi.Environment["SIX_STARS_LLM_MODEL"] = supervisor.ModelManifest.Models.Llm?.OpenAiModelName ?? "local-model";
        psi.Environment["SIX_STARS_SCREEN_LLM_MODEL"] = supervisor.ModelManifest.Models.Vision?.OpenAiModelName
            ?? supervisor.ModelManifest.Models.Llm?.OpenAiModelName
            ?? "local-model";
        psi.Environment["SIX_STARS_SCREEN_LLM_ENABLED"] = supervisor.VisionReady ? "true" : "false";
        psi.Environment["SIX_STARS_SCREEN_LLM_USE_JSON_SCHEMA"] = "false";
        psi.Environment["SIX_STARS_SCREEN_LLM_MAX_TOKENS"] = psi.Environment.TryGetValue("SIX_STARS_SCREEN_LLM_MAX_TOKENS", out var screenMaxTokens) ? screenMaxTokens : "0";
        psi.Environment["SIX_STARS_SCREEN_LLM_TIMEOUT_SECONDS"] = psi.Environment.TryGetValue("SIX_STARS_SCREEN_LLM_TIMEOUT_SECONDS", out var screenTimeout) ? screenTimeout : "120";
        // llama.cpp + small local Gemma models are more stable with prompt-only JSON
        // than strict json_schema grammar mode. Do not send per-request max_tokens by default;
        // prompt contracts and reasoning-off control should make the model stop on final JSON.
        psi.Environment["SIX_STARS_LLM_USE_JSON_SCHEMA"] = psi.Environment.TryGetValue("SIX_STARS_LLM_USE_JSON_SCHEMA", out var jsonSchema) ? jsonSchema : "false";
        psi.Environment["SIX_STARS_LLM_MAX_TOKENS"] = psi.Environment.TryGetValue("SIX_STARS_LLM_MAX_TOKENS", out var maxTokens) ? maxTokens : "0";
        psi.Environment["SIX_STARS_LLM_SPEECH_MAX_TOKENS"] = psi.Environment.TryGetValue("SIX_STARS_LLM_SPEECH_MAX_TOKENS", out var speechMaxTokens) ? speechMaxTokens : "0";
        psi.Environment["SIX_STARS_LLM_REVIEW_MAX_TOKENS"] = psi.Environment.TryGetValue("SIX_STARS_LLM_REVIEW_MAX_TOKENS", out var reviewMaxTokens) ? reviewMaxTokens : "0";
        psi.Environment["SIX_STARS_LLM_STORY_PLAN_MAX_TOKENS"] = psi.Environment.TryGetValue("SIX_STARS_LLM_STORY_PLAN_MAX_TOKENS", out var storyMaxTokens) ? storyMaxTokens : "0";
        psi.Environment["SIX_STARS_LLM_DISABLE_REASONING"] = psi.Environment.TryGetValue("SIX_STARS_LLM_DISABLE_REASONING", out var disableReasoning) ? disableReasoning : "true";
        psi.Environment["SIX_STARS_LLM_REASONING_BUDGET"] = psi.Environment.TryGetValue("SIX_STARS_LLM_REASONING_BUDGET", out var reasoningBudget) ? reasoningBudget : "0";
        psi.Environment["SIX_STARS_LLM_REASONING_FORMAT"] = psi.Environment.TryGetValue("SIX_STARS_LLM_REASONING_FORMAT", out var reasoningFormat) ? reasoningFormat : "none";
        psi.Environment["SIX_STARS_LLM_SEND_REASONING_CONTROL_FIELDS"] = psi.Environment.TryGetValue("SIX_STARS_LLM_SEND_REASONING_CONTROL_FIELDS", out var sendReasoningControls) ? sendReasoningControls : "true";
        psi.Environment["SIX_STARS_SCREEN_LLM_DISABLE_REASONING"] = psi.Environment.TryGetValue("SIX_STARS_SCREEN_LLM_DISABLE_REASONING", out var screenDisableReasoning) ? screenDisableReasoning : "true";
        psi.Environment["SIX_STARS_SCREEN_LLM_REASONING_BUDGET"] = psi.Environment.TryGetValue("SIX_STARS_SCREEN_LLM_REASONING_BUDGET", out var screenReasoningBudget) ? screenReasoningBudget : "0";
        psi.Environment["SIX_STARS_SCREEN_LLM_REASONING_FORMAT"] = psi.Environment.TryGetValue("SIX_STARS_SCREEN_LLM_REASONING_FORMAT", out var screenReasoningFormat) ? screenReasoningFormat : "none";
        psi.Environment["SIX_STARS_SCREEN_LLM_SEND_REASONING_CONTROL_FIELDS"] = psi.Environment.TryGetValue("SIX_STARS_SCREEN_LLM_SEND_REASONING_CONTROL_FIELDS", out var screenSendReasoningControls) ? screenSendReasoningControls : "true";
        psi.Environment["SIX_STARS_WHISPER_ENDPOINT"] = "http://127.0.0.1:8000/v1/audio/transcriptions";
        psi.Environment["SIX_STARS_WEBSOCKET_TTS_URL"] = "ws://127.0.0.1:8100/tts/stream";
        psi.Environment["SIX_STARS_USE_STREAMING_TTS"] = "true";
        // Keep the render device open with silent PCM while TTS is generating/between chunks.
        // This prevents HDMI/USB/digital outputs from going idle before the real speech WAV arrives.
        psi.Environment["SIX_STARS_AUDIO_KEEP_ALIVE"] = psi.Environment.TryGetValue("SIX_STARS_AUDIO_KEEP_ALIVE", out var audioKeepAlive) ? audioKeepAlive : "true";
        psi.Environment["SIX_STARS_AUDIO_KEEPALIVE_BACKEND"] = psi.Environment.TryGetValue("SIX_STARS_AUDIO_KEEPALIVE_BACKEND", out var audioKeepAliveBackend) ? audioKeepAliveBackend : "directsound";
        psi.Environment["SIX_STARS_REQUIRE_WAKE_WORD"] = psi.Environment.TryGetValue("SIX_STARS_REQUIRE_WAKE_WORD", out var wake) ? wake : "false";
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not launch UI: " + uiExe);
    }

    internal static void ApplySharedEnvironment(IDictionary<string, string?> env, string root, ModelManifest models)
    {
        var modelsRoot = Path.Combine(root, "models");
        env["SIX_STARS_MODELS_ROOT"] = modelsRoot;
        env["HF_HOME"] = Path.Combine(modelsRoot, "hf");
        env["HF_HUB_CACHE"] = Path.Combine(modelsRoot, "hf", "hub");
        env["PYTHONNOUSERSITE"] = "1";
        env["WHISPER_PORT"] = "8000";
        env["WHISPER_HOST"] = "127.0.0.1";
        env["WHISPER_MODEL"] = models.Models.Asr?.ModelName ?? "large-v3-turbo";
        env["WHISPER_DOWNLOAD_ROOT"] = Path.Combine(modelsRoot, "asr");
        env["QWEN_TTS_PORT"] = "8100";
        env["QWEN_TTS_MODEL_PATH"] = models.Models.Tts?.LocalPath is { Length: > 0 } ttsPath
            ? FullPath(root, ttsPath)
            : (models.Models.Tts?.ModelName ?? "Qwen/Qwen3-TTS-12Hz-0.6B-Base");
        env["QWEN_TTS_LANGUAGE"] = "English";
        env["QWEN_TTS_CHUNK_GENERATION_MODE"] = "first_then_prefetch";
        env["QWEN_TTS_MAX_PARALLEL_CHUNKS"] = "1";
        env["QWEN_TTS_USE_FASTER_QWEN3"] = env.TryGetValue("QWEN_TTS_USE_FASTER_QWEN3", out var faster) ? faster : "true";
        env["QWEN_TTS_USE_CUDA_GRAPHS"] = env.TryGetValue("QWEN_TTS_USE_CUDA_GRAPHS", out var graphs) ? graphs : "true";
    }

    internal static string FullPath(string root, string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
}

internal sealed record LauncherOptions(string? ManifestPath, string? ModelsPath, string? UiPath, string Profile, bool SkipInstall, bool ForceInstall, bool InstallOnly, bool NoUi, int WaitSeconds)
{
    public static LauncherOptions Parse(string[] args)
    {
        string? manifest = null;
        string? models = null;
        string? ui = null;
        string profile = "auto";
        bool skipInstall = false;
        bool forceInstall = false;
        bool installOnly = false;
        bool noUi = false;
        int waitSeconds = 90;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest": manifest = args[++i]; break;
                case "--models": models = args[++i]; break;
                case "--ui": ui = args[++i]; break;
                case "--profile": profile = args[++i]; break;
                case "--skip-install": skipInstall = true; break;
                case "--force-install": forceInstall = true; break;
                case "--install-only": installOnly = true; break;
                case "--no-ui": noUi = true; break;
                case "--wait-seconds": waitSeconds = int.Parse(args[++i]); break;
            }
        }

        return new LauncherOptions(manifest, models, ui, profile, skipInstall, forceInstall, installOnly, noUi, waitSeconds);
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
    public bool VisionReady { get; private set; }

    public async Task StartAllAsync(int waitSeconds)
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs", "services"));
        await StartKindAsync("llm", waitSeconds);
        await StartKindAsync("asr", Math.Min(waitSeconds, 45));
        await StartKindAsync("tts", 5); // WebSocket health is not HTTP; launch and let UI connect.
    }

    private async Task StartKindAsync(string kind, int waitSeconds)
    {
        var order = _manifest.SelectionOrder.TryGetValue(kind, out var ids)
            ? ids
            : _manifest.Packs.Where(p => p.Kind == kind).Select(p => p.Id).ToList();

        foreach (var id in order)
        {
            var pack = _manifest.Packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (pack == null || !string.Equals(pack.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsCompatible(pack)) continue;

            try
            {
                var proc = StartPack(pack);
                if (pack.HealthUrl != null && pack.HealthUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await WaitForHealthAsync(pack.HealthUrl, TimeSpan.FromSeconds(waitSeconds)))
                    {
                        Console.WriteLine($"Health check failed for {pack.Id}; trying next fallback.");
                        Kill(proc);
                        continue;
                    }
                }
                else
                {
                    await Task.Delay(1500);
                    if (proc.Process.HasExited)
                    {
                        Console.WriteLine($"{pack.Id} exited immediately with code {proc.Process.ExitCode}; trying next fallback.");
                        proc.Dispose();
                        continue;
                    }
                }

                _processes.Add(proc);
                if (kind == "llm")
                {
                    OpenAiChatUrl = pack.OpenAiChatUrl ?? "http://127.0.0.1:8080/v1/chat/completions";
                    UpdateVisionReadyForStartedLlm(pack);
                }
                Console.WriteLine($"READY   {kind}: {pack.Id}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SKIP    {pack.Id}: {ex.Message}");
            }
        }

        Console.WriteLine($"WARNING No {kind} service started. The UI may fail or run in text-only/fallback mode.");
    }

    private bool IsCompatible(RuntimePack pack)
    {
        return pack.Vendor.ToLowerInvariant() switch
        {
            "nvidia" => _hardware.HasNvidia,
            "generic-gpu" => _hardware.HasVulkan || _hardware.HasNvidia,
            "cpu" => true,
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
        if (pack.Id.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            env["WHISPER_DEVICE"] = "cuda";
            env["WHISPER_COMPUTE_TYPE"] = "float16";
            env["QWEN_TTS_DEVICE"] = "cuda";
        }
        else if (pack.Id.Contains("cpu", StringComparison.OrdinalIgnoreCase))
        {
            env["WHISPER_DEVICE"] = "cpu";
            env["WHISPER_COMPUTE_TYPE"] = "int8";
            env["QWEN_TTS_DEVICE"] = "cpu";
            env["QWEN_TTS_USE_CUDA_GRAPHS"] = "false";
            env["QWEN_TTS_USE_FASTER_QWEN3"] = "false";
        }

        if (pack.Kind == "llm")
        {
            env["GGML_BACKEND_PATH"] = Program.FullPath(_root, pack.WorkingDirectory ?? ".");
        }
    }

    private string ResolveArgs(RuntimePack pack)
    {
        var args = pack.Args ?? string.Empty;
        if (pack.Kind == "llm")
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

    private static string AppendReasoningDisabledArgs(string args)
    {
        // Gemma 4 and other reasoning-capable local models can fill message.reasoning_content
        // and leave message.content empty. The companion needs immediate spoken JSON, so the
        // launcher disables reasoning globally for llama-server unless the user already chose
        // explicit reasoning args in runtime-manifest.json.
        if (!ContainsArgument(args, "--reasoning"))
        {
            args += " --reasoning off";
        }

        if (!ContainsArgument(args, "--reasoning-budget"))
        {
            args += " --reasoning-budget 0";
        }

        if (!ContainsArgument(args, "--reasoning-format"))
        {
            args += " --reasoning-format none";
        }

        return args.Trim();
    }

    private static bool ContainsArgument(string args, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;
        }

        return args.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(x => string.Equals(x, argumentName, StringComparison.OrdinalIgnoreCase));
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
            Console.WriteLine("VISION Screen vision is enabled, but config/models.json models.vision.localPath is empty. Screen vision will be disabled for this launch.");
            VisionReady = false;
            return args;
        }

        var mmprojPath = Program.FullPath(_root, vision.LocalPath);
        if (!File.Exists(mmprojPath))
        {
            Console.WriteLine("VISION mmproj not found. Screen vision will be disabled for this launch:");
            Console.WriteLine("       " + mmprojPath);
            Console.WriteLine("       Put the matching multimodal projector GGUF at models.vision.localPath or set models.vision.enabled=false.");
            VisionReady = false;
            return args;
        }

        VisionReady = true;
        Console.WriteLine("VISION enabled with mmproj: " + mmprojPath);
        return (args + " --mmproj " + Quote(mmprojPath)).Trim();
    }

    private void UpdateVisionReadyForStartedLlm(RuntimePack pack)
    {
        if (pack.Kind != "llm") return;

        var vision = ModelManifest.Models.Vision;
        if (vision == null || !vision.Enabled)
        {
            VisionReady = false;
            return;
        }

        if ((pack.Args ?? string.Empty).Contains("--mmproj", StringComparison.OrdinalIgnoreCase))
        {
            VisionReady = true;
            return;
        }

        VisionReady = !string.IsNullOrWhiteSpace(vision.LocalPath) && File.Exists(Program.FullPath(_root, vision.LocalPath));
    }

    private static string Quote(string value) => value.Contains(' ') ? "\"" + value + "\"" : value;

    private static async Task<bool> WaitForHealthAsync(string url, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode) return true;
            }
            catch { }
            await Task.Delay(1000);
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
        foreach (var process in _processes)
        {
            Kill(process);
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
            HasVulkan: CommandWorks("vulkaninfo", "--summary") || RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
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
    [JsonPropertyName("selectionOrder")] public Dictionary<string, List<string>> SelectionOrder { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("packs")] public List<RuntimePack> Packs { get; set; } = new();
    public static RuntimeManifest Load(string path) => JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(path), JsonOptions()) ?? new RuntimeManifest();
    internal static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}

internal sealed class RuntimePack
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("vendor")] public string Vendor { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("workingDirectory")] public string? WorkingDirectory { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
    [JsonPropertyName("healthUrl")] public string? HealthUrl { get; set; }
    [JsonPropertyName("openAiChatUrl")] public string? OpenAiChatUrl { get; set; }
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
    [JsonPropertyName("modelName")] public string? ModelName { get; set; }
    [JsonPropertyName("openAiModelName")] public string? OpenAiModelName { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}
