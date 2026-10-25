using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;
using YamlDotNet.RepresentationModel;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>One editable Cosmonarchy Plus config file inside a version's
/// checkout. The game reads plus.yml and cosmo_plus.conf from its own
/// directory first, then from plugins/ (see the Plus plugin's config.rs),
/// so the editor offers all four locations.</summary>
public sealed record ConfigFileOption(string Label, string RelativePath)
{
    public override string ToString() => Label;
}

/// <summary>The Config tab: edit the Plus gameplay config (plus.yml cost
/// overrides, gas-tier deltas, advanced-worker tuning, ultraload) and the
/// env-style cosmo_plus.conf for the selected installed version.</summary>
public partial class ConfigViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly VersionManager _versions = AppRuntime.Versions;

    public ObservableCollection<GameVersion> Installed { get; } = [];

    public IReadOnlyList<ConfigFileOption> Files { get; } =
    [
        new ConfigFileOption("plus.yml (game directory)", "plus.yml"),
        new ConfigFileOption("plus.yml (plugins/)", Path.Combine("plugins", "plus.yml")),
        new ConfigFileOption("cosmo_plus.conf (game directory)", "cosmo_plus.conf"),
        new ConfigFileOption("cosmo_plus.conf (plugins/)", Path.Combine("plugins", "cosmo_plus.conf")),
    ];

    [ObservableProperty]
    private GameVersion? _selectedInstall;

    [ObservableProperty]
    private ConfigFileOption _selectedFile = new("plus.yml (game directory)", "plus.yml");
    [ObservableProperty]
    private string _configText = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _hasChanges;

    /// <summary>The edited file's absolute path, or null when no version is
    /// selected. The file itself may not exist yet.</summary>
    public string? ConfigPath =>
        SelectedInstall is null
            ? null
            : Path.Combine(_settings.RootPath, SelectedInstall.Dir, SelectedFile.RelativePath);

    /// <summary>What was on disk when the editor loaded ("" = no file yet);
    /// HasChanges compares against this, not against the example seed.</summary>
    private string? _loadedText;

    /// <summary>True once a file exists for the current selection.</summary>
    public bool FileExists => !string.IsNullOrEmpty(ConfigPath) && File.Exists(ConfigPath);

    public bool CanEdit => SelectedInstall is not null;

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
        if (!string.Equals(value, _loadedText, StringComparison.Ordinal))
            HasChanges = true;
        else
            HasChanges = false;
    }

    /// <summary>Read the selected file back from disk (or seed the editor
    /// from the example config when the file does not exist yet).</summary>
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
            Status = "Install a version on the Play tab first — its config lives inside its checkout.";
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
            // Seed the editor with the example so a fresh checkout does not
            // start from a blank buffer; the file is only written on Save.
            _loadedText = "";
            ConfigText = ExampleContent();
            HasChanges = true;
            Status = $"No {Path.GetFileName(path)} in {SelectedInstall!.Name} yet — seeded from the example; Save creates it.";
        }
        OnPropertyChanged(nameof(FileExists));
    }

    /// <summary>Write the editor content to the selected file. Invalid YAML
    /// is rejected so a typo can never break the game's config parsing.</summary>
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
            Status = $"Saved {path}.";
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

    private static string FileExistsText(string path)
    {
        var name = Path.GetFileName(path);
        var where = Path.GetDirectoryName(path)?.EndsWith("plugins", StringComparison.Ordinal) == true
            ? "plugins/"
            : "game directory";
        return $"Loaded {name} ({where}). The game reads it at launch; restart the game to apply changes.";
    }

    /// <summary>The example config shipped with the Plus plugin project, used
    /// to seed new files. Falls back to a minimal skeleton.</summary>
    private static string ExampleContent()
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
        return """
            # Cosmonarchy Plus gameplay config (plus.yml).
            # See plus.yml.example in the plugin project for every key.

            # Absolute unit cost overrides (minerals / gas / build_time in frames):
            overrides:
              # - unit: Zerg Mutath Spire
              #   minerals: 250
              #   gas: 300

            # Gas extractor deltas per public tier:
            # gas_tier_deltas:
            #   "1":
            #     minerals: -100
            #     build_time_seconds: -10

            # Advanced (tier-2) worker tuning:
            # advanced_workers:
            #   minerals_delta: 0
            #   gas_delta: 0
            #   build_time_delta_seconds: 0

            # Ultraload — tier-2 worker passive:
            # ultraload:
            #   enabled: true
            #   speed_multiplier: 1.5
            #   depot_radius_px: 480
            """;
    }
}
