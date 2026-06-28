using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GposeManager;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Pose Manager Settings###PoseManagerConfigWindow")
    {
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(400, 580);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        this.configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Viewport Settings");
        ImGui.Separator();
        ImGui.Spacing();

        bool showGrid = configuration.ShowGrid;
        if (ImGui.Checkbox("Show Ground Grid", ref showGrid))
        {
            configuration.ShowGrid = showGrid;
            configuration.Save();
        }

        bool depthShading = configuration.EnableDepthShading;
        if (ImGui.Checkbox("Enable Depth Fading", ref depthShading))
        {
            configuration.EnableDepthShading = depthShading;
            configuration.Save();
        }

        bool showMetadata = configuration.ShowSceneOverviewMetadata;
        if (ImGui.Checkbox("Show Pose Metadata in Overview", ref showMetadata))
        {
            configuration.ShowSceneOverviewMetadata = showMetadata;
            configuration.Save();
        }

        bool showPreviewsMatching = configuration.ShowPreviewsInMatchingFolders;
        if (ImGui.Checkbox("Show Previews in Matching Folders", ref showPreviewsMatching))
        {
            configuration.ShowPreviewsInMatchingFolders = showPreviewsMatching;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("When searching or filtering by tags, display preview image files inside folders that contain matching poses or match search terms.");
        }

        ImGui.Spacing();

        float jointSize = configuration.JointSize;
        ImGui.SetNextItemWidth(-1);
        ImGui.Text("Mannequin Joint Size:");
        if (ImGui.SliderFloat("##JointSize", ref jointSize, 0.2f, 3.0f, "%.1f"))
        {
            configuration.JointSize = jointSize;
            configuration.Save();
        }

        float limbThickness = configuration.LimbThickness;
        ImGui.SetNextItemWidth(-1);
        ImGui.Text("Mannequin Limb Thickness:");
        if (ImGui.SliderFloat("##LimbThickness", ref limbThickness, 0.0f, 5.0f, "%.1f"))
        {
            configuration.LimbThickness = limbThickness;
            configuration.Save();
        }

        float cameraSens = configuration.CameraSensitivity;
        ImGui.SetNextItemWidth(-1);
        ImGui.Text("Camera Mouse Sensitivity:");
        if (ImGui.SliderFloat("##CameraSens", ref cameraSens, 0.2f, 3.0f, "%.1f"))
        {
            configuration.CameraSensitivity = cameraSens;
            configuration.Save();
        }

        ImGui.Spacing();

        Vector3 skeletonColor = configuration.SkeletonColor;
        ImGui.Text("Default Skeleton color:");
        if (ImGui.ColorEdit3("##SkeletonColor", ref skeletonColor))
        {
            configuration.SkeletonColor = skeletonColor;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Directories");
        ImGui.Separator();
        ImGui.Spacing();

        string defaultPosePath = configuration.DefaultPosePath;
        ImGui.Text("Default Poses Path:");
        
        float browseBtnWidth = 80f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - browseBtnWidth - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputText("##DefaultPosePath", ref defaultPosePath, 512))
        {
            configuration.DefaultPosePath = defaultPosePath;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Browse...##BrowseDefaultPoses"))
        {
            plugin.FileDialogManager.OpenFolderDialog(
                "Select Default Poses Folder",
                (success, path) =>
                {
                    if (success)
                    {
                        configuration.DefaultPosePath = path;
                        configuration.Save();
                    }
                }
            );
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "File Management & Safety");
        ImGui.Separator();
        ImGui.Spacing();

        bool enableFileMgmt = configuration.EnableFileManagement;
        if (ImGui.Checkbox("Enable Drag & Drop File/Folder Moving", ref enableFileMgmt))
        {
            configuration.EnableFileManagement = enableFileMgmt;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("WARNING: Allows moving files and folders on disk directly inside the Pose Browser.\nDisabled by default to prevent accidental moves.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Brio Integration");
        ImGui.Separator();
        ImGui.Spacing();

        bool brioLibraryIntegration = configuration.BrioLibraryIntegration;
        if (ImGui.Checkbox("Enable Brio \"Import Pose\" Mannequin", ref brioLibraryIntegration))
        {
            configuration.BrioLibraryIntegration = brioLibraryIntegration;
            configuration.Save();
            plugin.BrioIntegration.UpdateHookState();
        }

        if (brioLibraryIntegration)
        {
            int brioMode = configuration.BrioIntegrationMode;
            string[] modes = new[] { "Inject into Empty Space (Right Pane)", "Attached Window (Snaps to Side)" };
            ImGui.SetNextItemWidth(-1);
            ImGui.Text("Integration Mode:");
            if (ImGui.Combo("##BrioMode", ref brioMode, modes, modes.Length))
            {
                configuration.BrioIntegrationMode = brioMode;
                configuration.Save();
            }

            float mannequinScale = configuration.BrioMannequinScale;
            ImGui.SetNextItemWidth(-1);
            ImGui.Text("Mannequin Preview Scale:");
            if (ImGui.SliderFloat("##BrioMannequinScale", ref mannequinScale, 0.5f, 2.0f, "%.2f"))
            {
                configuration.BrioMannequinScale = mannequinScale;
                configuration.Save();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Support & Community");
        ImGui.Spacing();
        if (ImGui.Button("Join Support Discord"))
        {
            Dalamud.Utility.Util.OpenLink("https://discord.gg/PvxW4mXaWp");
        }
        ImGui.SameLine();
        if (ImGui.Button("Support on Ko-fi"))
        {
            Dalamud.Utility.Util.OpenLink("https://ko-fi.com/kararemy");
        }
    }
}
