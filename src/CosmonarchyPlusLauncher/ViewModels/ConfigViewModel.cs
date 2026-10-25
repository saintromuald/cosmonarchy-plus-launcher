using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;
using YamlDotNet.RepresentationModel;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>One editable config file. <see cref="IsGameConfig"/> files live
/// next to StarCraft.exe in the shared Starcraft folder (cosmonarchy.yml);
/// the others live inside the selected version's checkout and are read by
/// the Plus plugin (game directory first, then plugins/).</summary>
public sealed record ConfigFileOption(string Label, string RelativePath, bool IsGameConfig = false)
{
    public override string ToString() => Label;
}

/// <summary>The Config tab: edit the game's own config (cosmonarchy.yml —
/// hotkeys, random races, UI toggles, colors) and the Plus plugin's files
/// (plus.yml cost overrides and cosmo_plus.conf env-style switches).</summary>
public partial class ConfigViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly VersionManager _versions = AppRuntime.Versions;

    public ObservableCollection<GameVersion> Installed { get; } = [];

    public IReadOnlyList<ConfigFileOption> Files { get; } =
    [
        new ConfigFileOption("cosmonarchy.yml (game settings — hotkeys, races, UI)", "cosmonarchy.yml", IsGameConfig: true),
        new ConfigFileOption("plus.yml (game directory)", "plus.yml"),
        new ConfigFileOption("plus.yml (plugins/)", Path.Combine("plugins", "plus.yml")),
        new ConfigFileOption("cosmo_plus.conf (game directory)", "cosmo_plus.conf"),
        new ConfigFileOption("cosmo_plus.conf (plugins/)", Path.Combine("plugins", "cosmo_plus.conf")),
    ];

    [ObservableProperty]
    private GameVersion? _selectedInstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameConfigSelected))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private ConfigFileOption _selectedFile = new("cosmonarchy.yml (game settings — hotkeys, races, UI)", "cosmonarchy.yml", IsGameConfig: true);

    [ObservableProperty]
    private string _configText = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _hasChanges;

    /// <summary>The edited file's absolute path, or null when it cannot be
    /// resolved (no version selected for a per-version file). The file
    /// itself may not exist yet.</summary>
    public string? ConfigPath
    {
        get
        {
            if (SelectedFile.IsGameConfig)
                return Path.Combine(_settings.RootPath, "Starcraft", SelectedFile.RelativePath);
            return SelectedInstall is null
                ? null
                : Path.Combine(_settings.RootPath, SelectedInstall.Dir, SelectedFile.RelativePath);
        }
    }

    public bool IsGameConfigSelected => SelectedFile.IsGameConfig;

    /// <summary>The shared game config needs no version; the Plus files do.</summary>
    public bool CanEdit => IsGameConfigSelected || SelectedInstall is not null;

    /// <summary>What was on disk when the editor loaded ("" = no file yet);
    /// HasChanges compares against this, not against the example seed.</summary>
    private string? _loadedText;

    public ConfigViewModel()
    {
        // Must reference an item of Files so the ComboBox selection binds.
        SelectedFile = Files[0];
        ReloadVersions();
        LoadFromDisk();
    }

    /// <summary>Re-scan the installed versions (installs/removals on the
    /// Play tab change the list) and re-pick the currently selected one.</summary>
    private void ReloadVersions()
    {
        var selectedId = SelectedInstall?.Id ?? _versions.Selected?.Id;
        Installed.Clear();
        foreach (var version in _versions.Versions)
            Installed.Add(version);
        SelectedInstall = Installed.FirstOrDefault(v => v.Id == selectedId) ?? Installed.FirstOrDefault();
    }

    partial void OnSelectedInstallChanged(GameVersion? value)
    {
        if (value is not null)
            _versions.Select(value.Id);
        LoadFromDisk();
    }

    partial void OnSelectedFileChanged(ConfigFileOption value) => LoadFromDisk();

    partial void OnConfigTextChanged(string value)
    {
        HasChanges = !string.Equals(value, _loadedText, StringComparison.Ordinal);
    }

    /// <summary>Read the selected file back from disk (or seed the editor
    /// from a template when the file does not exist yet).</summary>
    [RelayCommand]
    private void Load()
    {
        ReloadVersions();
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        if (ConfigPath is not { } path)
        {
            ConfigText = "";
            _loadedText = "";
            HasChanges = false;
            Status = "Install a version on the Play tab first — the Plus config files live inside its checkout.";
            return;
        }

        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            _loadedText = text;
            ConfigText = text;
            HasChanges = false;
            Status = FileExistsText(path);
        }
        else
        {
            // Seed the editor with a template so a fresh install does not
            // start from a blank buffer; the file is only written on Save.
            _loadedText = "";
            ConfigText = TemplateFor(path);
            HasChanges = true;
            var where = IsGameConfigSelected
                ? "the Starcraft folder (the game creates it with defaults on launch)"
                : SelectedInstall?.Name ?? "the version";
            Status = $"No {Path.GetFileName(path)} in {where} yet — seeded from a template; Save creates it.";
        }
    }

    /// <summary>Write the editor content to the selected file. Invalid YAML
    /// is rejected so a typo can never break the game's config parser.</summary>
    [RelayCommand]
    private void Save()
    {
        if (ConfigPath is not { } path)
        {
            Status = "Install a version first.";
            return;
        }
        if (!TryValidate(ConfigText, out var error))
        {
            Status = $"Not saved — {error}";
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ConfigText);
            _loadedText = ConfigText;
            HasChanges = false;
            Status = IsGameConfigSelected
                ? $"Saved {path}. Close the game first when editing — it rewrites this file on exit."
                : $"Saved {path}. The plugin reads it at launch; restart the game to apply changes.";
        }
        catch (Exception ex)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    /// <summary>Syntax-check the YAML before writing it.</summary>
    private static bool TryValidate(string text, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
            return true; // an empty config file is legal (all defaults)
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
            return true;
        }
        catch (Exception ex)
        {
            error = $"invalid YAML: {ex.Message}";
            return false;
        }
    }

    private string FileExistsText(string path)
    {
        var name = Path.GetFileName(path);
        if (IsGameConfigSelected)
            return $"Loaded {name} (Starcraft folder — shared by every installed version). Close the game before editing: it rewrites this file when it exits.";
        var where = Path.GetDirectoryName(path)?.EndsWith("plugins", StringComparison.Ordinal) == true
            ? "plugins/"
            : "game directory";
        return $"Loaded {name} ({where}). The game reads it at launch; restart the game to apply changes.";
    }

    /// <summary>A template for files that do not exist yet: the Plus example
    /// config for plus.yml, a commented skeleton otherwise.</summary>
    private string TemplateFor(string path)
    {
        if (Path.GetFileName(path) == "plus.yml")
        {
            var example = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Projects", "cosmonarchy-plus", "plus", "plus.yml");
            try
            {
                if (File.Exists(example))
                    return File.ReadAllText(example);
            }
            catch (IOException)
            {
            }
        }

        if (IsGameConfigSelected)
        {
            return """
                # Cosmonarchy config — cosmonarchy.yml
                # Lives next to StarCraft.exe and is shared by every installed version.
                # The game writes this file with its defaults on launch if it is missing.

                # Random settings
                # Possible values: Zerg, Terran, Protoss, Askosi, All, Original
                # You must select at least two values or All will be used
                random-races:
                  - All
                avoid-mirror-races: true

                # Replay Kraken (self-hosted fork)
                replay-kraken-server: http://cosmonarchy.com
                replay-kraken-token: ""
                replay-kraken-upload-all: false

                # Launch settings
                disable-wmode-prompt: true
                prefer-wmode: false
                allow-multiple-instances: false

                # Localization
                # BCP47 language tag, e.g. ko-KR. Missing translations fall back to English
                language: en-US

                # General UI
                extended-camera-hotkeys: true
                show-timer: true
                show-apm: true
                show-perf-stats: false
                always-show-extended-tooltips: false
                suppress-windows-key: false

                # Hotkeys
                quick-casting: false
                quick-building: false
                grid-hotkeys:
                  enabled: true
                  layout:
                    - QWE
                    - ASD
                    - ZXC
                # Adds extra control groups at R, F, V, T, G, B. Must be used with grid hotkeys!
                extended-control-groups: true
                default-to-steal-hotkeys: false
                # Replaces tilde with an additional control group, moves console to F12
                tilde-control-group: true
                flip-attack-and-patrol: true

                # Replays
                auto-save-replays: true
                sanitize-replay-chat: false

                # Maps
                # Use single backslashes for paths!
                # The map directory must be within Starcraft's 'maps' folder.
                default-map-directory: CMBW-Root\CMBW-maps
                """;
        }

        return """
            # Cosmonarchy Plus env-style config (cosmo_plus.conf).
            # The plugin reads the game directory first, then plugins/.

            # Mode switch (explicit; overrides the lobby game type):
            # mode: antimeta
            """;
    }
}
