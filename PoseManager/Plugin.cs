using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Interface.ImGuiFileDialog;

namespace GposeManager;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    private static readonly string[] Commands = { "/posemanager", "/posemngr", "/pmngr" };

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("PoseManager");
    private ViewportWindow ViewportWindow { get; init; }
    private ConfigWindow ConfigWindow { get; init; }
    public FileDialogManager FileDialogManager { get; } = new();
    public BrioIntegration BrioIntegration { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        ViewportWindow = new ViewportWindow(this);
        BrioIntegration = new BrioIntegration(this);
        
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(ViewportWindow);

        foreach (var cmd in Commands)
        {
            CommandManager.AddHandler(cmd, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open the Pose Manager window"
            });
        }

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += DrawFileDialog;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        PluginInterface.UiBuilder.DisableGposeUiHide = true;

        Log.Information($"PoseManager plugin initialized.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= DrawFileDialog;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        ViewportWindow.Dispose();
        BrioIntegration.Dispose();

        foreach (var cmd in Commands)
        {
            CommandManager.RemoveHandler(cmd);
        }
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainUi();
    }

    public void ToggleMainUi() => ViewportWindow.Toggle();
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ResetCamera() => ViewportWindow.ResetCamera();
    private void DrawFileDialog() => FileDialogManager.Draw();
}
