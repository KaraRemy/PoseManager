using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace GposeManager;

[Serializable]
public class SerializedActor
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public System.Numerics.Vector3 OffsetPosition { get; set; } = System.Numerics.Vector3.Zero;
    public System.Numerics.Vector3 OffsetRotationEuler { get; set; } = System.Numerics.Vector3.Zero;
    public System.Numerics.Vector3 Color { get; set; } = new System.Numerics.Vector3(0.2f, 0.8f, 0.9f);
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public string DefaultPosePath { get; set; } = @"D:\Benutzer\Dokumente\Brio\Poses";

    // Saved Scene State
    public List<SerializedActor> SavedActors { get; set; } = new();
    public float CameraZoom { get; set; } = 2.5f;
    public float CameraYaw { get; set; } = 0.0f;
    public float CameraPitch { get; set; } = 0.15f;
    public System.Numerics.Vector3 CameraTarget { get; set; } = new System.Numerics.Vector3(0, 0.7f, 0);

    // Viewport and Rendering Options
    public bool ShowGrid { get; set; } = true;
    public float JointSize { get; set; } = 1.0f;
    public float LimbThickness { get; set; } = 3.0f;
    public bool EnableDepthShading { get; set; } = true;
    public float CameraSensitivity { get; set; } = 1.0f;
    public System.Numerics.Vector3 SkeletonColor { get; set; } = new System.Numerics.Vector3(0.2f, 0.8f, 0.9f);

    // Brio Integration
    public bool BrioLibraryIntegration { get; set; } = true;
    public int BrioIntegrationMode { get; set; } = 0; // 0 = Inject (Pane), 1 = Attached Window
    public float BrioMannequinScale { get; set; } = 1.0f;

    // UI Toggles & File Safety
    public bool ShowSceneOverviewMetadata { get; set; } = true;
    public bool ShowPreviewsInMatchingFolders { get; set; } = true;
    public bool EnableFileManagement { get; set; } = false;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
