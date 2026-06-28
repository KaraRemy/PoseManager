using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;

namespace GposeManager;

public class ViewportWindow : Window, IDisposable
{
    public class ActorInstance
    {
        public string Name = string.Empty;
        public string FilePath = string.Empty;
        public PoseData? Pose = null;
        public Vector3 OffsetPosition = Vector3.Zero;
        public Vector3 OffsetRotationEuler = Vector3.Zero; // in degrees (Pitch, Yaw, Roll)
        public Vector3 Color = new Vector3(0.2f, 0.8f, 0.9f);

        public Vector3 GetOffsetPosition(Vector3 bonePos)
        {
            float radPitch = (float)(OffsetRotationEuler.X * Math.PI / 180.0);
            float radYaw = (float)(OffsetRotationEuler.Y * Math.PI / 180.0);
            float radRoll = (float)(OffsetRotationEuler.Z * Math.PI / 180.0);

            // Create quaternion (Yaw is rotation around vertical Y axis, Pitch around X, Roll around Z)
            var q = Quaternion.CreateFromYawPitchRoll(radYaw, radPitch, radRoll);
            return OffsetPosition + Vector3.Transform(bonePos, q);
        }
    }

    private readonly Plugin plugin;
    private readonly Camera camera;
    private readonly List<ActorInstance> actors = new();
    private BrowserNode? rootNode = null;
    private string scannedPath = string.Empty;
    private string selectedBrowserPosePath = string.Empty;
    private bool resetPopupOpen = true;

    private string activeViewportImage = string.Empty;
    private string activeZoomImagePath = string.Empty;
    private string activeCmpFilePrompt = string.Empty;

    private readonly ConcurrentDictionary<string, (PoseMetadataOnly Metadata, DateTime LastWriteTime)> metadataCache = new();
    private bool isBackgroundCachingMetadata = false;
    private bool openEditMetadataPopup = false;
    private bool openCreateFolderPopup = false;
    private string createFolderParentPath = string.Empty;
    private string newFolderName = "New Folder";
    private bool openRenameItemPopup = false;
    private string renameItemSourcePath = string.Empty;
    private string renameItemNewName = string.Empty;
    private bool openDeleteFolderPopup = false;
    private string deleteFolderTarget = string.Empty;
    private bool openDeleteFilePopup = false;
    private string deleteFileTarget = string.Empty;
    private string browserSearchQuery = string.Empty;
    private string lastParsedSearchQuery = string.Empty;
    private string cachedSearchTextFilter = string.Empty;
    private List<string> cachedSearchTagFilters = new();
    private readonly HashSet<string> selectedBrowserPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool openBatchTagFilesPopup = false;
    private string batchTagFilesInput = string.Empty;
    private bool openClearTagsModal = false;
    private string clearTagsTargetFolder = string.Empty;
    private bool clearTagsIsFolderMode = false;
    private bool openTagPickerPopup = false;
    private bool openTagFolderPopup = false;
    private string tagFolderTarget = string.Empty;
    private string tagFolderInput = string.Empty;
    private string draggedSourcePath = string.Empty;
    private string pendingMoveSource = string.Empty;
    private string pendingMoveTarget = string.Empty;
    private string editPosePath = string.Empty;
    private string editAuthor = string.Empty;
    private string editVersion = string.Empty;
    private string editDescription = string.Empty;
    private string editTags = string.Empty;

    public ViewportWindow(Plugin plugin)
        : base("Pose Manager###PoseManagerViewportWindow", ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        this.camera = new Camera();

        // Load saved scene state
        LoadSceneState();

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, 450),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(1, 1),
            Click = _ => plugin.ToggleConfigUi(),
            ShowTooltip = () => ImGui.SetTooltip("Settings")
        });
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var config = plugin.Configuration;

        // Use ImGui table for responsive, independent three-column layout
        var avail = ImGui.GetContentRegionAvail();
        if (ImGui.BeginTable("three_column_viewport_layout", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable, avail))
        {
            ImGui.TableSetupColumn("BrowserCol", ImGuiTableColumnFlags.WidthFixed, 180.0f);
            ImGui.TableSetupColumn("ViewportCol", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("ActorsCol", ImGuiTableColumnFlags.WidthFixed, 260.0f);

            ImGui.TableNextRow();

            // --- Column 0: Pose Browser ---
            ImGui.TableNextColumn();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Pose Browser");
            ImGui.SameLine();

            float refreshBtnSize = ImGui.GetFrameHeight();
            float tagBtnSize = ImGui.GetFrameHeight();
            float itemSpacing = ImGui.GetStyle().ItemSpacing.X;
            float availW = ImGui.GetContentRegionAvail().X;
            float inputW = Math.Max(30f, availW - refreshBtnSize - tagBtnSize - (2 * itemSpacing) - 6f);

            ImGui.SetNextItemWidth(inputW);
            ImGui.InputTextWithHint("##browser_search", "Search...", ref browserSearchQuery, 128);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Search folders, poses, images, or tags (e.g. 'tag:solo,pair').");
            }

            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button(FontAwesomeIcon.Tags.ToIconString(), new Vector2(tagBtnSize, tagBtnSize)))
            {
                openTagPickerPopup = true;
            }
            ImGui.PopFont();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Filter by Tags");
            }

            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button(FontAwesomeIcon.Sync.ToIconString(), new Vector2(refreshBtnSize, refreshBtnSize)))
            {
                ScanPoseFolder();
            }
            ImGui.PopFont();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Refresh Browser");
            }
            ImGui.Separator();
            ImGui.Spacing();

            // Scrolling child area for the tree view to avoid parent scrolling
            using (var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("PoseBrowserTreeScroll", new Vector2(-1, -1), false))
            {
                if (child.Success)
                {
                    if (string.IsNullOrWhiteSpace(scannedPath) || scannedPath != config.DefaultPosePath)
                    {
                        ScanPoseFolder();
                    }

                    if (rootNode != null)
                    {
                        UpdateCachedSearchQuery();
                        foreach (var childNode in rootNode.Children)
                        {
                            DrawBrowserNode(childNode);
                        }

                        if (config.EnableFileManagement && !string.IsNullOrEmpty(scannedPath) && Directory.Exists(scannedPath))
                        {
                            float availY = ImGui.GetContentRegionAvail().Y;
                            float fillY = Math.Max(30f, availY);
                            ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, fillY));
                            if (ImGui.BeginDragDropTarget())
                            {
                                var payload = ImGui.AcceptDragDropPayload("POSE_BROWSER_ITEM");
                                unsafe
                                {
                                    if (payload.Handle != null && payload.IsDelivery())
                                    {
                                        pendingMoveSource = draggedSourcePath;
                                        pendingMoveTarget = scannedPath;
                                    }
                                }
                                ImGui.EndDragDropTarget();
                            }
                        }

                        if (ImGui.BeginPopupContextWindow("browser_root_context", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverExistingPopup))
                        {
                            if (ImGui.MenuItem("+ Create New Root Folder"))
                            {
                                createFolderParentPath = scannedPath;
                                newFolderName = "New Folder";
                                openCreateFolderPopup = true;
                            }
                            if (ImGui.MenuItem("Add Tags to Root Folder Poses..."))
                            {
                                tagFolderTarget = scannedPath;
                                tagFolderInput = string.Empty;
                                openTagFolderPopup = true;
                            }
                            if (ImGui.MenuItem("Clear All Tags from Root Folder Poses..."))
                            {
                                clearTagsIsFolderMode = true;
                                clearTagsTargetFolder = scannedPath;
                                openClearTagsModal = true;
                            }
                            if (ImGui.MenuItem("Paste Preview from Clipboard"))
                            {
                                PastePreviewFromClipboard(scannedPath);
                            }
                            ImGui.EndPopup();
                        }
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.8f, 0.4f, 0.4f, 1.0f), "No poses folder loaded.");
                        ImGui.TextWrapped("Configure default path in settings (cog icon).");
                    }
                }
            }

            // --- Column 1: Interactive Viewport (3D Wireframe or Image Preview) ---
            ImGui.TableNextColumn();

            if (!string.IsNullOrEmpty(activeViewportImage) && File.Exists(activeViewportImage))
            {
                DrawLargeImageViewport();
            }
            else if (!string.IsNullOrEmpty(activeCmpFilePrompt) && File.Exists(activeCmpFilePrompt))
            {
                DrawCmpConversionViewport();
            }
            else
            {
                Draw3DSkeletonViewport();
            }

            // --- Column 2: Scene Overview (Actors) ---
            ImGui.TableNextColumn();

            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Scene Overview");
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - 4);
            ImGui.PushFont(UiBuilder.IconFont);
            bool showMeta = config.ShowSceneOverviewMetadata;
            var eyeIcon = showMeta ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
            if (ImGui.Button(eyeIcon.ToIconString(), new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight())))
            {
                config.ShowSceneOverviewMetadata = !showMeta;
                config.Save();
            }
            ImGui.PopFont();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(showMeta ? "Hide Pose Metadata" : "Show Pose Metadata");
            }
            ImGui.Separator();
            ImGui.Spacing();

            // Render actors inside a scrolling child area to prevent vertical overflow pushing window contents
            using (var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("ActorsListScroll", new Vector2(-1, -1), false))
            {
                if (child.Success)
                {
                    for (int i = 0; i < actors.Count; i++)
                    {
                        var actor = actors[i];
                        ImGui.PushID($"actor_{i}");

                        bool isHeaderOpen = ImGui.CollapsingHeader($"{actor.Name}##header", ImGuiTreeNodeFlags.DefaultOpen);
                        if (isHeaderOpen)
                        {
                            // Display the pose name and path simply
                            ImGui.Text("Pose:");
                            ImGui.SameLine();
                            float colorBtnSize = ImGui.GetFrameHeight();
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - colorBtnSize - 4);
                            var actorColor = actor.Color;
                            if (ImGui.ColorEdit3($"##color_{i}", ref actorColor, ImGuiColorEditFlags.NoInputs))
                            {
                                actor.Color = actorColor;
                            }
                            if (ImGui.IsItemDeactivatedAfterEdit())
                            {
                                SaveSceneState();
                            }
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip("Skeleton Color");
                            }
                            if (string.IsNullOrEmpty(actor.FilePath))
                            {
                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "None Loaded");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), Path.GetFileName(actor.FilePath));
                                if (config.ShowSceneOverviewMetadata && actor.Pose != null)
                                {
                                    ImGui.Indent(10f);
                                    if (!string.IsNullOrEmpty(actor.Pose.Author))
                                    {
                                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Author: ");
                                        ImGui.SameLine();
                                        ImGui.Text(actor.Pose.Author);
                                    }
                                    if (!string.IsNullOrEmpty(actor.Pose.Version))
                                    {
                                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Version: ");
                                        ImGui.SameLine();
                                        ImGui.Text(actor.Pose.Version);
                                    }
                                    if (actor.Pose.Tags != null && actor.Pose.Tags.Count > 0)
                                    {
                                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Tags: ");
                                        ImGui.SameLine();
                                        for (int t = 0; t < actor.Pose.Tags.Count; t++)
                                        {
                                            ImGui.TextColored(new Vector4(0.3f, 0.7f, 0.9f, 1.0f), $"[{actor.Pose.Tags[t]}]");
                                            if (t < actor.Pose.Tags.Count - 1) ImGui.SameLine();
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(actor.Pose.Description))
                                    {
                                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Desc: ");
                                        ImGui.SameLine();
                                        ImGui.TextWrapped(actor.Pose.Description);
                                    }
                                    ImGui.Spacing();
                                     if (ImGui.Button("Edit Tags & Info", new Vector2(ImGui.GetContentRegionAvail().X - 4, 0)))
                                    {
                                        editPosePath = actor.FilePath;
                                        editAuthor = actor.Pose.Author ?? string.Empty;
                                        editVersion = actor.Pose.Version ?? string.Empty;
                                        editDescription = actor.Pose.Description ?? string.Empty;
                                        editTags = actor.Pose.Tags != null ? string.Join(", ", actor.Pose.Tags) : string.Empty;
                                        openEditMetadataPopup = true;
                                    }
                                    ImGui.Unindent(10f);
                                }
                                ImGui.Spacing();
                                if (ImGui.Button("Clear"))
                                {
                                    actor.FilePath = string.Empty;
                                    actor.Pose = null;
                                    SaveSceneState();
                                }
                            }

                            // If loaded, allow fine-tuning positions and rotations
                            if (actor.Pose != null)
                            {
                                ImGui.Spacing();
                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Position Offset:");
                                var pos = actor.OffsetPosition;
                                if (ImGui.DragFloat3("##pos_offset", ref pos, 0.02f, -10.0f, 10.0f, "%.2f"))
                                {
                                    actor.OffsetPosition = pos;
                                }
                                if (ImGui.IsItemDeactivatedAfterEdit())
                                {
                                    SaveSceneState();
                                }

                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Rotation (Pitch/Yaw/Roll):");
                                var rot = actor.OffsetRotationEuler;
                                if (ImGui.DragFloat3("##rot_offset", ref rot, 0.5f, -180.0f, 180.0f, "%.1f°"))
                                {
                                    actor.OffsetRotationEuler = rot;
                                }
                                if (ImGui.IsItemDeactivatedAfterEdit())
                                {
                                    SaveSceneState();
                                }
                            }

                            // Delete Actor button (always keep at least one actor)
                            if (actors.Count > 1)
                            {
                                ImGui.Spacing();
                                if (ImGui.Button("Remove Actor", new Vector2(-1, 0)))
                                {
                                    actors.RemoveAt(i);
                                    SaveSceneState();
                                    ImGui.PopID();
                                    break;
                                }
                            }
                            ImGui.Separator();
                        }
                        ImGui.PopID();
                    }

                    ImGui.Spacing();
                    float btnSize = ImGui.GetFrameHeight();
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button(FontAwesomeIcon.Plus.ToIconString(), new Vector2(btnSize, btnSize)))
                    {
                        actors.Add(new ActorInstance { Name = $"Actor {actors.Count + 1}", Color = GetNextActorColor() });
                        SaveSceneState();
                    }
                    ImGui.PopFont();

                    ImGui.SameLine();
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button(FontAwesomeIcon.Trash.ToIconString(), new Vector2(btnSize, btnSize)))
                    {
                        ImGui.OpenPopup("ResetSceneConfirmation");
                    }
                    ImGui.PopFont();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Reset Scene");
                    }

                    resetPopupOpen = true;
                    if (ImGui.BeginPopupModal("ResetSceneConfirmation", ref resetPopupOpen, ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        ImGui.Text("Are you sure you want to reset the scene?\nThis will clear all actors and reset the camera.");
                        ImGui.Separator();

                        if (ImGui.Button("Yes, Reset", new Vector2(120, 0)))
                        {
                            ResetScene();
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Cancel", new Vector2(120, 0)))
                        {
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.EndPopup();
                    }
                }
            }

            ImGui.EndTable();
        }
        DrawEditMetadataModal();
        DrawCreateFolderModal();
        DrawRenameItemModal();
        DrawDeleteFolderModal();
        DrawSearchTagPickerPopup();
        DrawTagFolderModal();
        DrawBatchTagFilesModal();
        DrawClearTagsModal();
        DrawDeleteFileModal();

        if (!string.IsNullOrEmpty(pendingMoveSource) && !string.IsNullOrEmpty(pendingMoveTarget))
        {
            MoveFileSystemItem(pendingMoveSource, pendingMoveTarget);
            pendingMoveSource = string.Empty;
            pendingMoveTarget = string.Empty;
        }

        if (!string.IsNullOrEmpty(activeZoomImagePath) && File.Exists(activeZoomImagePath))
        {
            DrawFullScaleZoomOverlay(activeZoomImagePath);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            {
                activeZoomImagePath = string.Empty;
            }
        }
    }

    public void ResetCamera()
    {
        camera.Reset();
        SaveSceneState();
    }

    private void LoadPoseFile(ActorInstance actor, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var pose = JsonSerializer.Deserialize<PoseData>(json);

            if (pose != null)
            {
                actor.FilePath = path;
                actor.Pose = pose;

                // Set initial offsets from ModelDifference if non-zero
                var diffPos = pose.ModelDifference.Position;
                var diffRot = pose.ModelDifference.Rotation;
                var euler = QuaternionToEuler(diffRot);

                actor.OffsetPosition = diffPos;
                actor.OffsetRotationEuler = euler * (float)(180.0 / Math.PI);
                
                SaveSceneState();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to parse pose file {path}: {ex.Message}");
        }
    }

    private void SaveSceneState()
    {
        var config = plugin.Configuration;
        config.SavedActors = actors.Select(a => new SerializedActor
        {
            Name = a.Name,
            FilePath = a.FilePath,
            OffsetPosition = a.OffsetPosition,
            OffsetRotationEuler = a.OffsetRotationEuler,
            Color = a.Color
        }).ToList();

        config.CameraZoom = camera.Zoom;
        config.CameraYaw = camera.Yaw;
        config.CameraPitch = camera.Pitch;
        config.CameraTarget = camera.Target;

        config.Save();
    }

    private void LoadSceneState()
    {
        var config = plugin.Configuration;
        actors.Clear();
        if (config.SavedActors != null && config.SavedActors.Count > 0)
        {
            foreach (var sa in config.SavedActors)
            {
                var actor = new ActorInstance
                {
                    Name = sa.Name,
                    FilePath = sa.FilePath,
                    OffsetPosition = sa.OffsetPosition,
                    OffsetRotationEuler = sa.OffsetRotationEuler,
                    Color = sa.Color
                };
                if (!string.IsNullOrEmpty(sa.FilePath) && File.Exists(sa.FilePath))
                {
                    LoadPoseDataOnly(actor, sa.FilePath);
                }
                actors.Add(actor);
            }
        }
        else
        {
            // Populate defaults
            actors.Add(new ActorInstance { Name = "Actor 1", Color = config.SkeletonColor });
        }

        // Restore camera
        camera.Zoom = config.CameraZoom;
        camera.Yaw = config.CameraYaw;
        camera.Pitch = config.CameraPitch;
        camera.Target = config.CameraTarget;
    }

    private void LoadPoseDataOnly(ActorInstance actor, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var pose = JsonSerializer.Deserialize<PoseData>(json);
            if (pose != null)
            {
                actor.Pose = pose;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to parse pose file {path}: {ex.Message}");
        }
    }

    private void ResetScene()
    {
        actors.Clear();
        actors.Add(new ActorInstance { Name = "Actor 1", Color = plugin.Configuration.SkeletonColor });
        camera.Reset();
        SaveSceneState();
    }

    private static Vector3 QuaternionToEuler(Quaternion q)
    {
        // Convert quaternion to euler angles (Pitch, Yaw, Roll)
        double sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
        double cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        double pitch = Math.Atan2(sinr_cosp, cosr_cosp);

        double sinp = 2 * (q.W * q.Y - q.Z * q.X);
        double yaw;
        if (Math.Abs(sinp) >= 1)
            yaw = Math.CopySign(Math.PI / 2, sinp);
        else
            yaw = Math.Asin(sinp);

        double siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
        double cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        double roll = Math.Atan2(siny_cosp, cosy_cosp);

        return new Vector3((float)pitch, (float)yaw, (float)roll);
    }

    private Vector3 GetNextActorColor()
    {
        var actorColors = new[]
        {
            plugin.Configuration.SkeletonColor,
            new Vector3(0.9f, 0.3f, 0.6f), // Pink
            new Vector3(0.3f, 0.8f, 0.3f), // Lime
            new Vector3(0.9f, 0.7f, 0.1f), // Gold
            new Vector3(0.7f, 0.5f, 0.9f)  // Lavender
        };
        return actorColors[actors.Count % actorColors.Length];
    }

    public class BrowserNode
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public List<BrowserNode> Children { get; set; } = new();
    }

    private void ScanPoseFolder()
    {
        var path = plugin.Configuration.DefaultPosePath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            rootNode = null;
            scannedPath = string.Empty;
            return;
        }

        rootNode = BuildTree(path);
        scannedPath = path;
    }

    private BrowserNode BuildTree(string path)
    {
        var node = new BrowserNode
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = true
        };

        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                node.Children.Add(BuildTree(dir));
            }

            var files = Directory.GetFiles(path);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".pose" || ext == ".cmp")
                {
                    node.Children.Add(new BrowserNode
                    {
                        Name = Path.GetFileName(file),
                        FullPath = file,
                        IsDirectory = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error scanning pose directory {path}: {ex.Message}");
        }

        node.Children.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return b.IsDirectory.CompareTo(a.IsDirectory);
            }
            
            if (!a.IsDirectory)
            {
                var extA = Path.GetExtension(a.FullPath);
                var extB = Path.GetExtension(b.FullPath);
                bool aIsPose = extA.Equals(".pose", StringComparison.OrdinalIgnoreCase) || extA.Equals(".cmp", StringComparison.OrdinalIgnoreCase);
                bool bIsPose = extB.Equals(".pose", StringComparison.OrdinalIgnoreCase) || extB.Equals(".cmp", StringComparison.OrdinalIgnoreCase);
                
                if (aIsPose != bIsPose)
                {
                    return aIsPose ? -1 : 1;
                }
            }
            
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return node;
    }

    private bool MoveFileSystemItem(string sourcePath, string targetDirectory)
    {
        try
        {
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetDirectory)) return false;
            if (!Directory.Exists(targetDirectory)) return false;

            string itemName = Path.GetFileName(sourcePath);
            if (string.IsNullOrEmpty(itemName)) return false;

            bool isDir = Directory.Exists(sourcePath);
            bool isFile = File.Exists(sourcePath);

            if (!isDir && !isFile) return false;

            if (isDir)
            {
                string normSource = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normTarget = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (normTarget.Equals(normSource, StringComparison.OrdinalIgnoreCase) ||
                    normTarget.StartsWith(normSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.Warning($"[PM] Cannot move directory into itself or its subdirectory.");
                    return false;
                }
            }

            string destPath = Path.Combine(targetDirectory, itemName);
            if (sourcePath.Equals(destPath, StringComparison.OrdinalIgnoreCase)) return false;

            if (File.Exists(destPath) || Directory.Exists(destPath))
            {
                string nameWithoutExt = isDir ? itemName : Path.GetFileNameWithoutExtension(itemName);
                string ext = isDir ? string.Empty : Path.GetExtension(itemName);
                int count = 1;
                do
                {
                    destPath = Path.Combine(targetDirectory, $"{nameWithoutExt}_moved{count}{ext}");
                    count++;
                } while (File.Exists(destPath) || Directory.Exists(destPath));
            }

            if (isDir)
            {
                Directory.Move(sourcePath, destPath);
            }
            else
            {
                File.Move(sourcePath, destPath);
            }

            Plugin.Log.Information($"[PM] Moved {itemName} -> {destPath}");
            
            if (selectedBrowserPosePath == sourcePath) selectedBrowserPosePath = destPath;
            if (activeViewportImage == sourcePath) activeViewportImage = destPath;
            if (activeZoomImagePath == sourcePath) activeZoomImagePath = destPath;

            foreach (var actor in actors)
            {
                if (actor.FilePath == sourcePath)
                {
                    actor.FilePath = destPath;
                }
            }

            scannedPath = string.Empty; // Force tree rescan
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to move item {sourcePath} to {targetDirectory}: {ex.Message}");
            return false;
        }
    }

    private void DrawBrowserNode(BrowserNode node, BrowserNode? parentDirNode = null)
    {
        bool isSearching = !string.IsNullOrWhiteSpace(browserSearchQuery);
        if (isSearching)
        {
            if (!DoesNodeMatchSearch(node, cachedSearchTextFilter, cachedSearchTagFilters, parentDirNode))
            {
                return;
            }
        }

        if (node.IsDirectory)
        {
            var treeFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
            if (isSearching) treeFlags |= ImGuiTreeNodeFlags.DefaultOpen;

            bool isOpen = ImGui.TreeNodeEx($"##{node.FullPath}", treeFlags);
            
            if (plugin.Configuration.EnableFileManagement)
            {
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
                {
                    draggedSourcePath = node.FullPath;
                    ImGui.SetDragDropPayload("POSE_BROWSER_ITEM", new byte[] { 1 });
                    ImGui.Text($"Move Folder: {node.Name}");
                    ImGui.EndDragDropSource();
                }

                if (ImGui.BeginDragDropTarget())
                {
                    var payload = ImGui.AcceptDragDropPayload("POSE_BROWSER_ITEM");
                    unsafe
                    {
                        if (payload.Handle != null && payload.IsDelivery())
                        {
                            pendingMoveSource = draggedSourcePath;
                            pendingMoveTarget = node.FullPath;
                        }
                    }
                    ImGui.EndDragDropTarget();
                }
            }

            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            var folderIcon = isOpen ? FontAwesomeIcon.FolderOpen : FontAwesomeIcon.Folder;
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), folderIcon.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.Text(node.Name);

            if (ImGui.BeginPopupContextItem($"dir_context_{node.FullPath}"))
            {
                if (ImGui.MenuItem("+ Create New Subfolder"))
                {
                    createFolderParentPath = node.FullPath;
                    newFolderName = "New Folder";
                    openCreateFolderPopup = true;
                }
                if (ImGui.MenuItem("Add Tags to Folder Poses..."))
                {
                    tagFolderTarget = node.FullPath;
                    tagFolderInput = string.Empty;
                    openTagFolderPopup = true;
                }
                if (ImGui.MenuItem("Clear All Tags from Folder Poses..."))
                {
                    clearTagsIsFolderMode = true;
                    clearTagsTargetFolder = node.FullPath;
                    openClearTagsModal = true;
                }
                if (ImGui.MenuItem("Paste Preview from Clipboard"))
                {
                    PastePreviewFromClipboard(node.FullPath);
                }

                if (plugin.Configuration.EnableFileManagement && node.FullPath != scannedPath)
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Rename Folder"))
                    {
                        renameItemSourcePath = node.FullPath;
                        renameItemNewName = node.Name;
                        openRenameItemPopup = true;
                    }
                    string? containerDir = Path.GetDirectoryName(node.FullPath);
                    if (!string.IsNullOrEmpty(containerDir) && !containerDir.Equals(scannedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        string? parentTarget = Path.GetDirectoryName(containerDir);
                        if (!string.IsNullOrEmpty(parentTarget) && Directory.Exists(parentTarget))
                        {
                            if (ImGui.MenuItem("Move Up to Parent Folder"))
                            {
                                pendingMoveSource = node.FullPath;
                                pendingMoveTarget = parentTarget;
                            }
                        }
                    }
                    ImGui.Separator();
                    if (ImGui.MenuItem("Delete Folder"))
                    {
                        bool isEmpty = false;
                        try
                        {
                            isEmpty = Directory.GetFileSystemEntries(node.FullPath).Length == 0;
                        }
                        catch { }

                        if (isEmpty)
                        {
                            try
                            {
                                Directory.Delete(node.FullPath, false);
                                Plugin.Log.Information($"[PM] Deleted empty folder: {node.FullPath}");
                                scannedPath = string.Empty; // Force tree rescan
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.Error($"[PM] Failed to delete empty folder {node.FullPath}: {ex.Message}");
                            }
                        }
                        else
                        {
                            deleteFolderTarget = node.FullPath;
                            openDeleteFolderPopup = true;
                        }
                    }
                }
                ImGui.EndPopup();
            }

            if (isOpen)
            {
                foreach (var child in node.Children)
                {
                    DrawBrowserNode(child, node);
                }
                ImGui.TreePop();
            }
        }
        else
        {
            var ext = Path.GetExtension(node.FullPath).ToLowerInvariant();
            bool isPose = (ext == ".pose" || ext == ".cmp");
            
            ImGui.PushID(node.FullPath);
            
            var startPos = ImGui.GetCursorPos();
            bool isSelected = selectedBrowserPaths.Contains(node.FullPath) || (node.FullPath == selectedBrowserPosePath);
            
            if (ImGui.Selectable($"##sel_{node.FullPath}", isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
            {
                bool isCtrl = ImGui.GetIO().KeyCtrl;
                if (isCtrl)
                {
                    if (selectedBrowserPaths.Contains(node.FullPath))
                    {
                        selectedBrowserPaths.Remove(node.FullPath);
                    }
                    else
                    {
                        selectedBrowserPaths.Add(node.FullPath);
                    }
                }
                else
                {
                    selectedBrowserPaths.Clear();
                    selectedBrowserPaths.Add(node.FullPath);
                }
                selectedBrowserPosePath = node.FullPath;

                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (ext == ".cmp")
                    {
                        activeViewportImage = string.Empty;
                        activeCmpFilePrompt = node.FullPath;
                    }
                    else if (ext == ".pose")
                    {
                        activeViewportImage = string.Empty;
                        activeCmpFilePrompt = string.Empty;
                        if (actors.Count > 0)
                        {
                            LoadPoseFile(actors[0], node.FullPath);
                        }
                    }
                    else
                    {
                        activeViewportImage = node.FullPath;
                        activeCmpFilePrompt = string.Empty;
                    }
                }
            }

            if (plugin.Configuration.EnableFileManagement)
            {
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
                {
                    draggedSourcePath = node.FullPath;
                    ImGui.SetDragDropPayload("POSE_BROWSER_ITEM", new byte[] { 1 });
                    ImGui.Text($"Move File: {node.Name}");
                    ImGui.EndDragDropSource();
                }
            }

            if (ImGui.BeginPopupContextItem($"context_{node.FullPath}"))
            {
                if (!selectedBrowserPaths.Contains(node.FullPath))
                {
                    if (!ImGui.GetIO().KeyCtrl)
                    {
                        selectedBrowserPaths.Clear();
                    }
                    selectedBrowserPaths.Add(node.FullPath);
                    selectedBrowserPosePath = node.FullPath;
                }

                int selectedPoseCount = selectedBrowserPaths.Count(p => p.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase));
                if (selectedPoseCount > 1)
                {
                    if (ImGui.MenuItem($"Add Tags to Selected Poses ({selectedPoseCount} files)..."))
                    {
                        batchTagFilesInput = string.Empty;
                        openBatchTagFilesPopup = true;
                    }
                    if (ImGui.MenuItem($"Clear All Tags from Selected Poses ({selectedPoseCount} files)..."))
                    {
                        clearTagsIsFolderMode = false;
                        clearTagsTargetFolder = string.Empty;
                        openClearTagsModal = true;
                    }
                    ImGui.Separator();
                }

                if (isPose)
                {
                    selectedBrowserPosePath = node.FullPath;
                    for (int i = 0; i < actors.Count; i++)
                    {
                        var actor = actors[i];
                        if (ImGui.MenuItem($"Import to {actor.Name} (Mannequin)"))
                        {
                            activeViewportImage = string.Empty;
                            LoadPoseFile(actor, node.FullPath);
                        }
                    }
                    if (ImGui.MenuItem("+ Add New Mannequin Actor"))
                    {
                        var newActor = new ActorInstance { Name = $"Actor {actors.Count + 1}", Color = GetNextActorColor() };
                        actors.Add(newActor);
                        activeViewportImage = string.Empty;
                        LoadPoseFile(newActor, node.FullPath);
                    }

                    ImGui.Separator();

                    if (ext == ".pose" && ImGui.MenuItem("Edit Tags & Metadata"))
                    {
                        editPosePath = node.FullPath;
                        PoseMetadataOnly? meta = null;
                        if (metadataCache.TryGetValue(node.FullPath, out var cached))
                        {
                            meta = cached.Metadata;
                        }
                        else if (File.Exists(node.FullPath))
                        {
                            try
                            {
                                meta = JsonSerializer.Deserialize<PoseMetadataOnly>(File.ReadAllText(node.FullPath));
                            }
                            catch { }
                        }

                        editAuthor = meta?.Author ?? string.Empty;
                        editVersion = meta?.Version ?? string.Empty;
                        editDescription = meta?.Description ?? string.Empty;
                        editTags = meta?.Tags != null ? string.Join(", ", meta.Tags) : string.Empty;
                        openEditMetadataPopup = true;
                    }
                    if (ImGui.MenuItem("Paste Preview from Clipboard"))
                    {
                        string? parentDir = Path.GetDirectoryName(node.FullPath);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            PastePreviewFromClipboard(parentDir, node.FullPath);
                        }
                    }

                    ImGui.Separator();

                    bool inGPose = Plugin.ClientState.IsGPosing;
                    var brioActors = inGPose ? BrioReflectionHelper.GetBrioActors() : new List<BrioReflectionHelper.BrioActorTarget>();
                    bool canApply = inGPose && brioActors.Count > 0;

                    if (ImGui.BeginMenu("Apply to Brio Actor", canApply))
                    {
                        foreach (var brioActor in brioActors)
                        {
                            if (ImGui.BeginMenu(brioActor.Name))
                            {
                                if (ImGui.MenuItem("Full Pose"))
                                {
                                    BrioReflectionHelper.ApplyPoseToBrioActor(brioActor.PosingCapObj, node.FullPath, 0);
                                }
                                if (ImGui.MenuItem("Body Only"))
                                {
                                    BrioReflectionHelper.ApplyPoseToBrioActor(brioActor.PosingCapObj, node.FullPath, 2);
                                }
                                if (ImGui.MenuItem("Expression Only"))
                                {
                                    BrioReflectionHelper.ApplyPoseToBrioActor(brioActor.PosingCapObj, node.FullPath, 1);
                                }
                                ImGui.EndMenu();
                            }
                        }
                        ImGui.EndMenu();
                    }
                    if (ImGui.IsItemHovered() && !canApply)
                    {
                        ImGui.SetTooltip(!inGPose ? "Requires active GPose mode." : "No active actors found in Brio.");
                    }
                }

                if (!isPose)
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Delete Image"))
                    {
                        deleteFileTarget = node.FullPath;
                        openDeleteFilePopup = true;
                    }
                }

                if (plugin.Configuration.EnableFileManagement)
                {
                    if (isPose) ImGui.Separator();
                    if (ImGui.MenuItem("Rename File"))
                    {
                        renameItemSourcePath = node.FullPath;
                        renameItemNewName = node.Name;
                        openRenameItemPopup = true;
                    }

                    string? containerDir = Path.GetDirectoryName(node.FullPath);
                    if (!string.IsNullOrEmpty(containerDir) && !containerDir.Equals(scannedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        string? parentTarget = Path.GetDirectoryName(containerDir);
                        if (!string.IsNullOrEmpty(parentTarget) && Directory.Exists(parentTarget))
                        {
                            if (ImGui.MenuItem("Move Up to Parent Folder"))
                            {
                                pendingMoveSource = node.FullPath;
                                pendingMoveTarget = parentTarget;
                            }
                        }
                    }

                    if (isPose && ImGui.MenuItem("Delete Pose File"))
                    {
                        deleteFileTarget = node.FullPath;
                        openDeleteFilePopup = true;
                    }
                }

                ImGui.EndPopup();
            }

            bool isHovered = ImGui.IsItemHovered();
            if (isHovered && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            {
                string? zoomTarget = isPose ? null : node.FullPath;
                if (!string.IsNullOrEmpty(zoomTarget) && File.Exists(zoomTarget))
                {
                    activeZoomImagePath = zoomTarget;
                }
            }

            var endPos = ImGui.GetCursorPos();
            
            ImGui.SetCursorPos(startPos);
            
            ImGui.PushFont(UiBuilder.IconFont);
            if (isPose)
            {
                ImGui.TextColored(new Vector4(0.3f, 0.6f, 0.9f, 1.0f), FontAwesomeIcon.Running.ToIconString());
            }
            else
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), FontAwesomeIcon.Image.ToIconString());
            }
            ImGui.PopFont();
            
            ImGui.SameLine();
            ImGui.Text(node.Name);
            
            ImGui.SetCursorPos(endPos);

            if (isHovered && !ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            {
                if (isPose)
                {
                    DrawPoseTooltip(node);
                }
                else if (!isPose && File.Exists(node.FullPath))
                {
                    DrawImageTooltip(node.FullPath, node.Name);
                }
            }
            
            ImGui.PopID();
        }
    }

    private void DrawImageTooltip(string path, string title)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (texture != null)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), title);
            ImGui.Separator();
            
            float aspect = (float)texture.Width / Math.Max(1, texture.Height);
            float drawWidth = 250f;
            float drawHeight = drawWidth / aspect;
            
            ImGui.Image(texture.Handle, new Vector2(drawWidth, drawHeight));
            ImGui.EndTooltip();
        }
    }

    private void DrawPoseTooltip(BrowserNode node)
    {
        string path = node.FullPath;
        PoseMetadataOnly? meta = null;
        try
        {
            if (File.Exists(path))
            {
                var writeTime = File.GetLastWriteTime(path);
                if (metadataCache.TryGetValue(path, out var cached) && cached.LastWriteTime == writeTime)
                {
                    meta = cached.Metadata;
                }
                else
                {
                    var json = File.ReadAllText(path);
                    meta = JsonSerializer.Deserialize<PoseMetadataOnly>(json);
                    if (meta != null)
                    {
                        metadataCache[path] = (meta, writeTime);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to parse metadata for tooltip from {path}: {ex.Message}");
        }

        ImGui.BeginTooltip();
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), node.Name);
        ImGui.Separator();

        if (meta != null)
        {
            if (!string.IsNullOrEmpty(meta.Author))
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Author:");
                ImGui.SameLine();
                ImGui.Text(meta.Author);
            }
            if (!string.IsNullOrEmpty(meta.Version))
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Version:");
                ImGui.SameLine();
                ImGui.Text(meta.Version);
            }
            if (meta.Tags != null && meta.Tags.Count > 0)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Tags:");
                ImGui.SameLine();
                for (int i = 0; i < meta.Tags.Count; i++)
                {
                    ImGui.TextColored(new Vector4(0.3f, 0.7f, 0.9f, 1.0f), $"[{meta.Tags[i]}]");
                    if (i < meta.Tags.Count - 1) ImGui.SameLine();
                }
            }
            if (!string.IsNullOrEmpty(meta.Description))
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Description:");
                ImGui.TextWrapped(meta.Description);
            }
        }

        ImGui.EndTooltip();
    }

    private void DrawCmpConversionViewport()
    {
        float iconSize = ImGui.GetFrameHeight();
        
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Cube.ToIconString(), new Vector2(iconSize, iconSize)))
        {
            activeCmpFilePrompt = string.Empty;
            ImGui.PopFont();
            return;
        }
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Return to 3D Skeleton Mannequin Viewport");
        }

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), $"Concept Matrix Pose File: {Path.GetFileName(activeCmpFilePrompt)}");

        ImGui.Spacing();

        if (string.IsNullOrEmpty(activeCmpFilePrompt) || !File.Exists(activeCmpFilePrompt))
            return;

        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 50f) avail.X = 50f;
        if (avail.Y < 50f) avail.Y = 50f;

        var startPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(startPos, startPos + avail, ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.1f, 1.0f)));
        drawList.AddRect(startPos, startPos + avail, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.25f, 0.8f)), 4.0f, ImDrawFlags.None, 1.5f);

        float cardWidth = Math.Min(460f, avail.X * 0.9f);
        float cardHeight = 220f;

        float offsetX = (avail.X - cardWidth) * 0.5f;
        float offsetY = (avail.Y - cardHeight) * 0.5f;

        var startCursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startCursor.X + Math.Max(0, offsetX), startCursor.Y + Math.Max(0, offsetY)));

        if (ImGui.BeginChild("##CmpCardChild", new Vector2(cardWidth, cardHeight), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground))
        {
            float childW = ImGui.GetContentRegionAvail().X;

            ImGui.PushFont(UiBuilder.IconFont);
            string fileAltIcon = FontAwesomeIcon.FileCode.ToIconString();
            float iconW = ImGui.CalcTextSize(fileAltIcon).X;
            ImGui.SetCursorPosX((childW - iconW) * 0.5f);
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), fileAltIcon);
            ImGui.PopFont();

            ImGui.Spacing();

            string titleStr = "Concept Matrix (.cmp) Pose File";
            float titleW = ImGui.CalcTextSize(titleStr).X;
            ImGui.SetCursorPosX((childW - titleW) * 0.5f);
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), titleStr);

            ImGui.Spacing();

            string infoStr = "This pose uses the Concept Matrix (.cmp) format. While 3D wireframe preview is not supported for raw .cmp files, you can apply it directly to your character in GPose via Brio!";
            ImGui.TextWrapped(infoStr);

            ImGui.Spacing();
            ImGui.Spacing();

            bool inGPose = Plugin.ClientState.IsGPosing;
            var brioActors = inGPose ? BrioReflectionHelper.GetBrioActors() : new List<BrioReflectionHelper.BrioActorTarget>();
            bool canApply = inGPose && brioActors.Count > 0;

            float btnW = 220f;
            ImGui.SetCursorPosX((childW - btnW) * 0.5f);

            if (ImGui.BeginMenu("Apply to Brio Actor", canApply))
            {
                foreach (var brioActor in brioActors)
                {
                    if (ImGui.BeginMenu(brioActor.Name))
                    {
                        if (ImGui.MenuItem("Full Pose"))
                        {
                            BrioReflectionHelper.ApplyPoseToBrioActor(brioActor.PosingCapObj, activeCmpFilePrompt, 0);
                        }
                        if (ImGui.MenuItem("Body Only"))
                        {
                            BrioReflectionHelper.ApplyPoseToBrioActor(brioActor.PosingCapObj, activeCmpFilePrompt, 2);
                        }
                        if (ImGui.MenuItem("Expression Only"))
                        {
                            BrioReflectionHelper.ApplyPoseToBrioActor(brioActor.PosingCapObj, activeCmpFilePrompt, 1);
                        }
                        ImGui.EndMenu();
                    }
                }
                ImGui.EndMenu();
            }
            if (ImGui.IsItemHovered() && !canApply)
            {
                ImGui.SetTooltip(!inGPose ? "Requires active GPose mode." : "No active actors found in Brio.");
            }

            ImGui.EndChild();
        }
    }

    private void DrawEditMetadataModal()
    {
        if (openEditMetadataPopup)
        {
            ImGui.OpenPopup("EditPoseMetadataPopup");
            openEditMetadataPopup = false;
        }

        bool dummy = true;
        if (ImGui.BeginPopupModal("EditPoseMetadataPopup", ref dummy, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Edit Pose Metadata");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text("Author:");
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.CalcTextSize("Description:").X - ImGui.CalcTextSize("Author:").X);
            ImGui.InputText("##edit_author", ref editAuthor, 128);

            ImGui.Text("Version:");
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.CalcTextSize("Description:").X - ImGui.CalcTextSize("Version:").X);
            ImGui.InputText("##edit_version", ref editVersion, 32);

            ImGui.Text("Tags (comma separated):");
            ImGui.InputText("##edit_tags", ref editTags, 256);

            ImGui.Text("Description:");
            ImGui.InputTextMultiline("##edit_desc", ref editDescription, 1024, new Vector2(300, 80));

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Save", new Vector2(120, 0)))
            {
                var tagsList = editTags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                SavePoseMetadata(editPosePath, editAuthor, editVersion, editDescription, tagsList);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawCreateFolderModal()
    {
        if (openCreateFolderPopup)
        {
            ImGui.OpenPopup("CreateFolderPopup");
            openCreateFolderPopup = false;
        }

        bool dummy = true;
        if (ImGui.BeginPopupModal("CreateFolderPopup", ref dummy, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Create New Folder");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text("Folder Name:");
            ImGui.InputText("##new_folder_name", ref newFolderName, 128);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Create", new Vector2(120, 0)))
            {
                if (!string.IsNullOrWhiteSpace(newFolderName) && !string.IsNullOrEmpty(createFolderParentPath))
                {
                    string targetPath = Path.Combine(createFolderParentPath, newFolderName.Trim());
                    try
                    {
                        if (!Directory.Exists(targetPath))
                        {
                            Directory.CreateDirectory(targetPath);
                            scannedPath = string.Empty; // Rescan tree
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error($"Failed to create folder: {ex.Message}");
                    }
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private bool RenameFileSystemItem(string sourcePath, string newName)
    {
        try
        {
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrWhiteSpace(newName)) return false;

            bool isDir = Directory.Exists(sourcePath);
            bool isFile = File.Exists(sourcePath);
            if (!isDir && !isFile) return false;

            string? parentDir = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir)) return false;

            string targetPath = Path.Combine(parentDir, newName.Trim());
            if (sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)) return false;

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                Plugin.Log.Warning($"[PM] An item with name {newName} already exists.");
                return false;
            }

            if (isDir)
            {
                Directory.Move(sourcePath, targetPath);
            }
            else
            {
                File.Move(sourcePath, targetPath);
            }

            Plugin.Log.Information($"[PM] Renamed {sourcePath} -> {targetPath}");

            if (selectedBrowserPosePath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
                selectedBrowserPosePath = selectedBrowserPosePath.Replace(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);

            if (activeViewportImage.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
                activeViewportImage = activeViewportImage.Replace(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);

            if (activeZoomImagePath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
                activeZoomImagePath = activeZoomImagePath.Replace(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);

            foreach (var actor in actors)
            {
                if (!string.IsNullOrEmpty(actor.FilePath) && actor.FilePath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    actor.FilePath = actor.FilePath.Replace(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);
                }
            }

            scannedPath = string.Empty; // Force tree rescan
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to rename {sourcePath} to {newName}: {ex.Message}");
            return false;
        }
    }

    private void DrawRenameItemModal()
    {
        if (openRenameItemPopup)
        {
            ImGui.OpenPopup("RenameItemPopup");
            openRenameItemPopup = false;
        }

        bool dummy = true;
        if (ImGui.BeginPopupModal("RenameItemPopup", ref dummy, ImGuiWindowFlags.AlwaysAutoResize))
        {
            bool isDir = Directory.Exists(renameItemSourcePath);
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), isDir ? "Rename Folder" : "Rename File");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text("New Name:");
            ImGui.InputText("##rename_item_name", ref renameItemNewName, 128);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Rename", new Vector2(120, 0)))
            {
                if (!string.IsNullOrWhiteSpace(renameItemNewName) && !string.IsNullOrEmpty(renameItemSourcePath))
                {
                    RenameFileSystemItem(renameItemSourcePath, renameItemNewName);
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawDeleteFolderModal()
    {
        if (openDeleteFolderPopup)
        {
            ImGui.OpenPopup("DeleteFolderPopup");
            openDeleteFolderPopup = false;
        }

        bool dummy = true;
        if (ImGui.BeginPopupModal("DeleteFolderPopup", ref dummy, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1.0f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1.0f), "Delete Non-Empty Folder?");
            ImGui.Separator();
            ImGui.Spacing();

            string folderName = Path.GetFileName(deleteFolderTarget);
            ImGui.TextWrapped($"Are you sure you want to delete the folder \"{folderName}\"?");
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.4f, 1.0f), "WARNING: This folder contains files or subfolders.\nDeleting it will permanently delete all contents! Files cannot be recovered.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.1f, 0.1f, 1.0f));
            if (ImGui.Button("Delete Permanently", new Vector2(180, 0)))
            {
                try
                {
                    if (Directory.Exists(deleteFolderTarget))
                    {
                        Directory.Delete(deleteFolderTarget, true);
                        Plugin.Log.Information($"[PM] Deleted non-empty folder recursively: {deleteFolderTarget}");
                        scannedPath = string.Empty; // Force tree rescan
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"[PM] Failed to recursively delete folder {deleteFolderTarget}: {ex.Message}");
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopStyleColor(3);

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void ApplyTagsToFolder(string folderPath, List<string> newTags)
    {
        try
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath) || newTags.Count == 0) return;

            var poseFiles = Directory.GetFiles(folderPath, "*.pose", SearchOption.AllDirectories);
            int updatedCount = 0;

            foreach (var poseFile in poseFiles)
            {
                PoseMetadataOnly? meta = null;
                if (metadataCache.TryGetValue(poseFile, out var cached))
                {
                    meta = cached.Metadata;
                }
                else if (File.Exists(poseFile))
                {
                    try
                    {
                        meta = JsonSerializer.Deserialize<PoseMetadataOnly>(File.ReadAllText(poseFile));
                    }
                    catch { }
                }

                var existingTags = meta?.Tags ?? new List<string>();
                var mergedTags = new List<string>(existingTags);

                foreach (var tag in newTags)
                {
                    if (!mergedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        mergedTags.Add(tag);
                    }
                }

                SavePoseMetadata(poseFile, meta?.Author ?? string.Empty, meta?.Version ?? string.Empty, meta?.Description ?? string.Empty, mergedTags);
                updatedCount++;
            }

            Plugin.Log.Information($"[PM] Batch applied tags [{string.Join(", ", newTags)}] to {updatedCount} poses in {folderPath}");
            scannedPath = string.Empty; // Rescan tree
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to batch apply tags to folder {folderPath}: {ex.Message}");
        }
    }

    private void DrawTagFolderModal()
    {
        if (openTagFolderPopup)
        {
            ImGui.OpenPopup("TagFolderPopup");
            openTagFolderPopup = false;
        }

        bool dummy = true;
        if (ImGui.BeginPopupModal("TagFolderPopup", ref dummy, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Apply Tags to Folder Poses");
            ImGui.Separator();
            ImGui.Spacing();

            string folderName = Path.GetFileName(tagFolderTarget);
            if (string.IsNullOrEmpty(folderName)) folderName = "Root Folder";
            ImGui.Text($"Target Folder: {folderName}");
            ImGui.Spacing();

            ImGui.Text("Enter Tags to Add (comma-separated):");
            ImGui.SetNextItemWidth(380);
            ImGui.InputTextWithHint("##tag_folder_input", "e.g. nsfw, solo, standing", ref tagFolderInput, 256);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Apply Tags to Folder", new Vector2(240, 0)))
            {
                var tagsToAdd = tagFolderInput.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (tagsToAdd.Count > 0 && !string.IsNullOrEmpty(tagFolderTarget))
                {
                    ApplyTagsToFolder(tagFolderTarget, tagsToAdd);
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void ApplyTagsToSelectedFiles(List<string> newTags)
    {
        try
        {
            if (selectedBrowserPaths.Count == 0 || newTags.Count == 0) return;

            int updatedCount = 0;
            foreach (var poseFile in selectedBrowserPaths.ToList())
            {
                if (!poseFile.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) && !poseFile.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase)) continue;

                if (poseFile.EndsWith(".pose", StringComparison.OrdinalIgnoreCase))
                {
                    PoseMetadataOnly? meta = null;
                    if (metadataCache.TryGetValue(poseFile, out var cached))
                    {
                        meta = cached.Metadata;
                    }
                    else if (File.Exists(poseFile))
                    {
                        try
                        {
                            meta = JsonSerializer.Deserialize<PoseMetadataOnly>(File.ReadAllText(poseFile));
                        }
                        catch { }
                    }

                    var existingTags = meta?.Tags ?? new List<string>();
                    var mergedTags = new List<string>(existingTags);

                    foreach (var tag in newTags)
                    {
                        if (!mergedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        {
                            mergedTags.Add(tag);
                        }
                    }

                    SavePoseMetadata(poseFile, meta?.Author ?? string.Empty, meta?.Version ?? string.Empty, meta?.Description ?? string.Empty, mergedTags);
                    updatedCount++;
                }
            }

            Plugin.Log.Information($"[PM] Batch applied tags [{string.Join(", ", newTags)}] to {updatedCount} selected files.");
            scannedPath = string.Empty; // Rescan tree
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to batch apply tags to selected files: {ex.Message}");
        }
    }

    private void DrawBatchTagFilesModal()
    {
        if (openBatchTagFilesPopup)
        {
            ImGui.OpenPopup("BatchTagFilesPopup");
            openBatchTagFilesPopup = false;
        }

        bool dummy = true;
        if (ImGui.BeginPopupModal("BatchTagFilesPopup", ref dummy, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Apply Tags to Selected Files");
            ImGui.Separator();
            ImGui.Spacing();

            int poseCount = selectedBrowserPaths.Count(p => p.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase));
            ImGui.Text($"Selected Files: {poseCount} pose files");
            ImGui.Spacing();

            ImGui.Text("Enter Tags to Add (comma-separated):");
            ImGui.SetNextItemWidth(380);
            ImGui.InputTextWithHint("##batch_tag_files_input", "e.g. nsfw, solo, standing", ref batchTagFilesInput, 256);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Apply Tags to Selection", new Vector2(240, 0)))
            {
                var tagsToAdd = batchTagFilesInput.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (tagsToAdd.Count > 0)
                {
                    ApplyTagsToSelectedFiles(tagsToAdd);
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void ClearTagsInSelection()
    {
        try
        {
            if (selectedBrowserPaths.Count == 0) return;
            int updatedCount = 0;

            foreach (var poseFile in selectedBrowserPaths.ToList())
            {
                if (!poseFile.EndsWith(".pose", StringComparison.OrdinalIgnoreCase)) continue;

                PoseMetadataOnly? meta = null;
                if (metadataCache.TryGetValue(poseFile, out var cached))
                {
                    meta = cached.Metadata;
                }
                else if (File.Exists(poseFile))
                {
                    try
                    {
                        meta = JsonSerializer.Deserialize<PoseMetadataOnly>(File.ReadAllText(poseFile));
                    }
                    catch { }
                }

                SavePoseMetadata(poseFile, meta?.Author ?? string.Empty, meta?.Version ?? string.Empty, meta?.Description ?? string.Empty, new List<string>());
                updatedCount++;
            }

            Plugin.Log.Information($"[PM] Cleared all tags from {updatedCount} selected pose files.");
            scannedPath = string.Empty; // Rescan tree
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to clear tags from selected files: {ex.Message}");
        }
    }

    private void ClearTagsInFolder(string folderPath)
    {
        try
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            var poseFiles = Directory.GetFiles(folderPath, "*.pose", SearchOption.AllDirectories);
            int updatedCount = 0;

            foreach (var poseFile in poseFiles)
            {
                PoseMetadataOnly? meta = null;
                if (metadataCache.TryGetValue(poseFile, out var cached))
                {
                    meta = cached.Metadata;
                }
                else if (File.Exists(poseFile))
                {
                    try
                    {
                        meta = JsonSerializer.Deserialize<PoseMetadataOnly>(File.ReadAllText(poseFile));
                    }
                    catch { }
                }

                SavePoseMetadata(poseFile, meta?.Author ?? string.Empty, meta?.Version ?? string.Empty, meta?.Description ?? string.Empty, new List<string>());
                updatedCount++;
            }

            Plugin.Log.Information($"[PM] Cleared all tags from {updatedCount} pose files in {folderPath}.");
            scannedPath = string.Empty; // Rescan tree
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to clear tags in folder {folderPath}: {ex.Message}");
        }
    }

    private void DrawClearTagsModal()
    {
        if (openClearTagsModal)
        {
            ImGui.OpenPopup("ClearTagsConfirmationPopup");
            openClearTagsModal = false;
        }

        bool dummy = true;
        if (ImGui.BeginPopupModal("ClearTagsConfirmationPopup", ref dummy, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1.0f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1.0f), "Clear All Tags Confirmation");
            ImGui.Separator();
            ImGui.Spacing();

            if (clearTagsIsFolderMode)
            {
                string folderName = Path.GetFileName(clearTagsTargetFolder);
                if (string.IsNullOrEmpty(folderName)) folderName = "Root Folder";
                ImGui.TextWrapped($"Are you sure you want to remove ALL tags from all poses inside folder \"{folderName}\"?");
            }
            else
            {
                int poseCount = selectedBrowserPaths.Count(p => p.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase));
                ImGui.TextWrapped($"Are you sure you want to remove ALL tags from the {poseCount} selected pose files?");
            }

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.4f, 1.0f), "WARNING: Removed tags cannot be recovered.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.1f, 0.1f, 1.0f));
            if (ImGui.Button("Clear All Tags", new Vector2(140, 0)))
            {
                if (clearTagsIsFolderMode)
                {
                    ClearTagsInFolder(clearTagsTargetFolder);
                }
                else
                {
                    ClearTagsInSelection();
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopStyleColor(3);

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private static System.Drawing.Image? GetImageFromClipboardSTA()
    {
        System.Drawing.Image? img = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsImage())
                {
                    img = System.Windows.Forms.Clipboard.GetImage();
                }
            }
            catch { }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        return img;
    }

    private void PastePreviewFromClipboard(string targetFolderPath, string? targetPoseFilePath = null)
    {
        try
        {
            if (string.IsNullOrEmpty(targetFolderPath) || !Directory.Exists(targetFolderPath)) return;

            var img = GetImageFromClipboardSTA();
            if (img == null)
            {
                Plugin.Log.Warning("[PM] No image found on clipboard to paste.");
                return;
            }

            string destFileName;
            if (!string.IsNullOrEmpty(targetPoseFilePath))
            {
                destFileName = Path.GetFileNameWithoutExtension(targetPoseFilePath) + ".png";
            }
            else
            {
                string folderName = Path.GetFileName(targetFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(folderName)) folderName = "folder";

                destFileName = $"{folderName}.png";
                int count = 1;
                while (File.Exists(Path.Combine(targetFolderPath, destFileName)))
                {
                    destFileName = $"{folderName}_{count}.png";
                    count++;
                }
            }

            string destPath = Path.Combine(targetFolderPath, destFileName);
            img.Save(destPath, System.Drawing.Imaging.ImageFormat.Png);
            img.Dispose();

            Plugin.Log.Information($"[PM] Saved clipboard preview image to {destPath}");
            scannedPath = string.Empty; // Rescan tree
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to paste preview image from clipboard: {ex.Message}");
        }
    }

    private void DrawDeleteFileModal()
    {
        if (openDeleteFilePopup)
        {
            ImGui.OpenPopup("DeleteFilePopup");
            openDeleteFilePopup = false;
        }

        bool dummy = true;
        if (ImGui.BeginPopupModal("DeleteFilePopup", ref dummy, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1.0f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1.0f), "Delete File Confirmation");
            ImGui.Separator();
            ImGui.Spacing();

            string fileName = Path.GetFileName(deleteFileTarget);
            ImGui.TextWrapped($"Are you sure you want to delete the file \"{fileName}\"?");
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.4f, 1.0f), "WARNING: This file will be permanently deleted from disk and cannot be recovered.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.1f, 0.1f, 1.0f));
            if (ImGui.Button("Delete Permanently", new Vector2(180, 0)))
            {
                try
                {
                    if (File.Exists(deleteFileTarget))
                    {
                        File.Delete(deleteFileTarget);
                        Plugin.Log.Information($"[PM] Deleted file: {deleteFileTarget}");
                        if (activeViewportImage == deleteFileTarget) activeViewportImage = string.Empty;
                        if (activeZoomImagePath == deleteFileTarget) activeZoomImagePath = string.Empty;
                        if (selectedBrowserPosePath == deleteFileTarget) selectedBrowserPosePath = string.Empty;
                        selectedBrowserPaths.Remove(deleteFileTarget);
                        metadataCache.TryRemove(deleteFileTarget, out _);
                        scannedPath = string.Empty; // Force tree rescan
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"[PM] Failed to delete file {deleteFileTarget}: {ex.Message}");
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopStyleColor(3);

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void UpdateCachedSearchQuery()
    {
        if (browserSearchQuery == lastParsedSearchQuery) return;
        lastParsedSearchQuery = browserSearchQuery;
        var (text, tags) = ParseSearchQuery(browserSearchQuery);
        cachedSearchTextFilter = text;
        cachedSearchTagFilters = tags;
    }

    private (string TextFilter, List<string> TagFilters) ParseSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (string.Empty, new List<string>());

        var tagFilters = new List<string>();
        string textFilter = query;

        int tagIdx = query.IndexOf("tag:", StringComparison.OrdinalIgnoreCase);
        if (tagIdx >= 0)
        {
            string before = query.Substring(0, tagIdx).Trim();
            string afterTag = query.Substring(tagIdx + 4);
            int spaceIdx = afterTag.IndexOf(' ');
            string tagsPart = spaceIdx >= 0 ? afterTag.Substring(0, spaceIdx) : afterTag;
            string after = spaceIdx >= 0 ? afterTag.Substring(spaceIdx).Trim() : string.Empty;

            textFilter = (before + " " + after).Trim();
            var parts = tagsPart.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                string trimmed = p.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    tagFilters.Add(trimmed);
            }
        }

        return (textFilter, tagFilters);
    }

    private bool IsPoseNodeMatchingSearch(BrowserNode node, string textFilter, List<string> tagFilters)
    {
        var ext = Path.GetExtension(node.FullPath).ToLowerInvariant();
        bool isPose = (ext == ".pose" || ext == ".cmp");
        if (!isPose) return false;

        bool nameMatch = string.IsNullOrWhiteSpace(textFilter) ||
                         node.Name.Contains(textFilter, StringComparison.OrdinalIgnoreCase);

        if (!nameMatch) return false;

        if (tagFilters.Count > 0)
        {
            PoseMetadataOnly? meta = null;
            if (metadataCache.TryGetValue(node.FullPath, out var cached))
            {
                meta = cached.Metadata;
            }

            if (meta == null || meta.Tags == null || meta.Tags.Count == 0) return false;

            foreach (var reqTag in tagFilters)
            {
                if (!meta.Tags.Any(t => t.Equals(reqTag, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
        }

        return true;
    }

    private bool FolderContainsMatchingPose(BrowserNode dirNode, string textFilter, List<string> tagFilters)
    {
        if (!dirNode.IsDirectory) return false;
        foreach (var child in dirNode.Children)
        {
            if (child.IsDirectory)
            {
                if (FolderContainsMatchingPose(child, textFilter, tagFilters))
                    return true;
            }
            else
            {
                if (IsPoseNodeMatchingSearch(child, textFilter, tagFilters))
                    return true;
            }
        }
        return false;
    }

    private bool DoesNodeMatchSearch(BrowserNode node, string textFilter, List<string> tagFilters, BrowserNode? parentDirNode = null)
    {
        if (node.IsDirectory)
        {
            bool nameMatch = string.IsNullOrWhiteSpace(textFilter) || node.Name.Contains(textFilter, StringComparison.OrdinalIgnoreCase);
            if (nameMatch && tagFilters.Count == 0) return true;

            foreach (var child in node.Children)
            {
                if (DoesNodeMatchSearch(child, textFilter, tagFilters, node))
                    return true;
            }
            return false;
        }
        else
        {
            var ext = Path.GetExtension(node.FullPath).ToLowerInvariant();
            bool isPose = (ext == ".pose" || ext == ".cmp");

            if (isPose)
            {
                return IsPoseNodeMatchingSearch(node, textFilter, tagFilters);
            }
            else
            {
                // It's an image file (.png, .jpg, etc.)
                bool directNameMatch = string.IsNullOrWhiteSpace(textFilter) || node.Name.Contains(textFilter, StringComparison.OrdinalIgnoreCase);
                if (directNameMatch && tagFilters.Count == 0) return true;

                if (plugin.Configuration.ShowPreviewsInMatchingFolders && parentDirNode != null)
                {
                    bool folderNameMatch = string.IsNullOrWhiteSpace(textFilter) || parentDirNode.Name.Contains(textFilter, StringComparison.OrdinalIgnoreCase);
                    if (folderNameMatch && tagFilters.Count == 0) return true;

                    if (FolderContainsMatchingPose(parentDirNode, textFilter, tagFilters))
                        return true;
                }

                return false;
            }
        }
    }

    private void DrawSearchTagPickerPopup()
    {
        if (openTagPickerPopup)
        {
            StartBackgroundMetadataCache();
            ImGui.OpenPopup("SearchTagPickerPopup");
            openTagPickerPopup = false;
        }

        if (ImGui.BeginPopup("SearchTagPickerPopup"))
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "Filter by Tags");
            if (isBackgroundCachingMetadata)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), "(Scanning...)");
            }
            ImGui.Separator();
            ImGui.Spacing();

            var allTags = metadataCache.Values
                .SelectMany(m => m.Metadata?.Tags ?? Enumerable.Empty<string>())
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t)
                .ToList();

            if (allTags.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No metadata tags found.");
            }
            else
            {
                var activeTags = GetActiveSearchTags(browserSearchQuery);

                foreach (var tag in allTags)
                {
                    bool isSelected = activeTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
                    if (ImGui.Checkbox(tag, ref isSelected))
                    {
                        if (isSelected)
                        {
                            if (!activeTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                                activeTags.Add(tag);
                        }
                        else
                        {
                            activeTags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
                        }
                        UpdateSearchQueryWithTags(activeTags);
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                if (ImGui.Button("Clear Tag Filter", new Vector2(-1, 0)))
                {
                    activeTags.Clear();
                    UpdateSearchQueryWithTags(activeTags);
                }
            }

            ImGui.EndPopup();
        }
    }

    private List<string> GetActiveSearchTags(string query)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        int tagIdx = query.IndexOf("tag:", StringComparison.OrdinalIgnoreCase);
        if (tagIdx >= 0)
        {
            string afterTag = query.Substring(tagIdx + 4);
            int spaceIdx = afterTag.IndexOf(' ');
            string tagsPart = spaceIdx >= 0 ? afterTag.Substring(0, spaceIdx) : afterTag;
            var parts = tagsPart.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                string trimmed = p.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    result.Add(trimmed);
            }
        }
        return result;
    }

    private void UpdateSearchQueryWithTags(List<string> tags)
    {
        string textPart = string.Empty;
        if (!string.IsNullOrWhiteSpace(browserSearchQuery))
        {
            int tagIdx = browserSearchQuery.IndexOf("tag:", StringComparison.OrdinalIgnoreCase);
            if (tagIdx >= 0)
            {
                string before = browserSearchQuery.Substring(0, tagIdx).Trim();
                string afterTag = browserSearchQuery.Substring(tagIdx + 4);
                int spaceIdx = afterTag.IndexOf(' ');
                string after = spaceIdx >= 0 ? afterTag.Substring(spaceIdx).Trim() : string.Empty;
                textPart = (before + " " + after).Trim();
            }
            else
            {
                textPart = browserSearchQuery.Trim();
            }
        }

        if (tags.Count == 0)
        {
            browserSearchQuery = textPart;
        }
        else
        {
            string tagStr = "tag:" + string.Join(",", tags);
            browserSearchQuery = string.IsNullOrWhiteSpace(textPart) ? tagStr : $"{textPart} {tagStr}";
        }
    }

    private void EnsureMetadataCached(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".pose", StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(filePath)) return;

        var writeTime = File.GetLastWriteTime(filePath);
        if (metadataCache.TryGetValue(filePath, out var cached) && cached.LastWriteTime == writeTime)
            return;

        try
        {
            var meta = JsonSerializer.Deserialize<PoseMetadataOnly>(File.ReadAllText(filePath));
            if (meta != null)
            {
                metadataCache[filePath] = (meta, writeTime);
            }
        }
        catch { }
    }

    private void StartBackgroundMetadataCache()
    {
        if (isBackgroundCachingMetadata || rootNode == null) return;
        isBackgroundCachingMetadata = true;
        var nodeToCache = rootNode;
        Task.Run(() =>
        {
            try
            {
                CacheAllMetadataAsync(nodeToCache);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[PM] Background metadata caching error: {ex.Message}");
            }
            finally
            {
                isBackgroundCachingMetadata = false;
            }
        });
    }

    private void CacheAllMetadataAsync(BrowserNode? node)
    {
        if (node == null) return;
        if (node.IsDirectory)
        {
            foreach (var child in node.Children)
                CacheAllMetadataAsync(child);
        }
        else if (node.FullPath.EndsWith(".pose", StringComparison.OrdinalIgnoreCase))
        {
            EnsureMetadataCached(node.FullPath);
        }
    }

    private void SavePoseMetadata(string path, string author, string version, string description, List<string> tags)
    {
        try
        {
            if (!File.Exists(path)) return;

            var jsonText = File.ReadAllText(path);
            var rootNode = JsonNode.Parse(jsonText);
            if (rootNode is JsonObject jsonObj)
            {
                if (string.IsNullOrWhiteSpace(author))
                    jsonObj.Remove("Author");
                else
                    jsonObj["Author"] = JsonValue.Create(author);

                if (string.IsNullOrWhiteSpace(version))
                    jsonObj.Remove("Version");
                else
                    jsonObj["Version"] = JsonValue.Create(version);

                if (string.IsNullOrWhiteSpace(description))
                    jsonObj.Remove("Description");
                else
                    jsonObj["Description"] = JsonValue.Create(description);

                var tagsArray = new JsonArray();
                foreach (var tag in tags)
                {
                    tagsArray.Add(JsonValue.Create(tag));
                }
                jsonObj["Tags"] = tagsArray;

                var options = new JsonSerializerOptions { WriteIndented = true };
                var updatedJson = jsonObj.ToJsonString(options);
                File.WriteAllText(path, updatedJson);

                foreach (var actor in actors)
                {
                    if (actor.FilePath == path && actor.Pose != null)
                    {
                        actor.Pose.Author = string.IsNullOrWhiteSpace(author) ? null : author;
                        actor.Pose.Version = string.IsNullOrWhiteSpace(version) ? null : version;
                        actor.Pose.Description = string.IsNullOrWhiteSpace(description) ? null : description;
                        actor.Pose.Tags = tags;
                    }
                }

                var writeTime = File.GetLastWriteTime(path);
                var metaOnly = new PoseMetadataOnly
                {
                    Author = string.IsNullOrWhiteSpace(author) ? null : author,
                    Version = string.IsNullOrWhiteSpace(version) ? null : version,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description,
                    Tags = tags
                };
                metadataCache[path] = (metaOnly, writeTime);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to save metadata to {path}: {ex.Message}");
        }
    }

    private void Draw3DSkeletonViewport()
    {
        var config = plugin.Configuration;
        float iconSize = ImGui.GetFrameHeight();
        
        // Reset Camera button
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Home.ToIconString(), new Vector2(iconSize, iconSize)))
        {
            camera.Reset();
        }
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Reset Camera View");
        }

        ImGui.SameLine();

        // Toggle Grid button
        bool showGrid = config.ShowGrid;
        if (showGrid) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.6f, 0.4f, 0.8f));
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Th.ToIconString(), new Vector2(iconSize, iconSize)))
        {
            config.ShowGrid = !config.ShowGrid;
            config.Save();
        }
        ImGui.PopFont();
        if (showGrid) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Toggle Ground Grid");
        }

        ImGui.Spacing();

        var canvasStart = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();
        
        if (canvasSize.X < 50.0f) canvasSize.X = 50.0f;
        if (canvasSize.Y < 50.0f) canvasSize.Y = 50.0f;

        ImGui.InvisibleButton("viewport_canvas", canvasSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        
        bool isHovered = ImGui.IsItemHovered();
        bool isActive = ImGui.IsItemActive();

        bool saveCamera = false;
        if (isActive)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                camera.Yaw -= delta.X * 0.007f * config.CameraSensitivity;
                camera.Pitch = Math.Clamp(camera.Pitch + delta.Y * 0.007f * config.CameraSensitivity, -1.45f, 1.45f);
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
            }
            else if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
            {
                var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Right);
                float cosYaw = (float)Math.Cos(camera.Yaw);
                float sinYaw = (float)Math.Sin(camera.Yaw);
                
                var right = new Vector3(cosYaw, 0, -sinYaw);
                camera.Target += (right * (-delta.X * 0.0012f * camera.Zoom) + Vector3.UnitY * (delta.Y * 0.0012f * camera.Zoom)) * config.CameraSensitivity;
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Right);
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) || ImGui.IsMouseReleased(ImGuiMouseButton.Right))
            {
                saveCamera = true;
            }
        }

        if (isHovered)
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0.0f)
            {
                camera.Zoom = Math.Clamp(camera.Zoom - wheel * 0.2f, 0.5f, 15.0f);
                saveCamera = true;
            }
        }

        if (saveCamera)
        {
            SaveSceneState();
        }

        camera.CanvasStart = canvasStart;
        camera.CanvasSize = canvasSize;

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(canvasStart, canvasStart + canvasSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.1f, 1.0f)));
        SkeletonRenderer.Render(drawList, camera, config, actors);
        drawList.AddRect(canvasStart, canvasStart + canvasSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.25f, 0.8f)), 4.0f, ImDrawFlags.None, 1.5f);

        string hudText = "Left Drag: Orbit | Right Drag: Pan | Scroll: Zoom";
        drawList.AddText(canvasStart + new Vector2(8, 8), ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.6f, 0.8f)), hudText);
    }

    private void DrawLargeImageViewport()
    {
        float iconSize = ImGui.GetFrameHeight();
        
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Cube.ToIconString(), new Vector2(iconSize, iconSize)))
        {
            activeViewportImage = string.Empty;
            ImGui.PopFont();
            return;
        }
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Return to 3D Skeleton Mannequin Viewport");
        }

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), $"Image Preview: {Path.GetFileName(activeViewportImage)}");

        ImGui.Spacing();

        if (string.IsNullOrEmpty(activeViewportImage) || !File.Exists(activeViewportImage))
            return;

        var texture = Plugin.TextureProvider.GetFromFile(activeViewportImage).GetWrapOrDefault();
        if (texture != null)
        {
            var avail = ImGui.GetContentRegionAvail();
            if (avail.X < 50f) avail.X = 50f;
            if (avail.Y < 50f) avail.Y = 50f;

            float aspect = (float)texture.Width / Math.Max(1, texture.Height);
            float drawW = avail.X;
            float drawH = drawW / aspect;

            if (drawH > avail.Y)
            {
                drawH = avail.Y;
                drawW = drawH * aspect;
            }

            float offsetX = (avail.X - drawW) * 0.5f;
            float offsetY = (avail.Y - drawH) * 0.5f;

            var startPos = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(startPos.X + offsetX, startPos.Y + offsetY));
            ImGui.Image(texture.Handle, new Vector2(drawW, drawH));
        }
        else
        {
            ImGui.TextColored(new Vector4(0.8f, 0.4f, 0.4f, 1.0f), "Failed to load image preview.");
        }
    }

    private void DrawFullScaleZoomOverlay(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return;
        var texture = Plugin.TextureProvider.GetFromFile(imagePath).GetWrapOrDefault();
        if (texture == null) return;

        var viewport = ImGui.GetMainViewport();
        var displaySize = viewport.Size;
        var drawList = ImGui.GetForegroundDrawList();

        drawList.AddRectFilled(Vector2.Zero, displaySize, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.75f)));

        float maxW = displaySize.X * 0.90f;
        float maxH = displaySize.Y * 0.90f;
        float aspect = (float)texture.Width / Math.Max(1, texture.Height);

        float drawW = maxW;
        float drawH = drawW / aspect;

        if (drawH > maxH)
        {
            drawH = maxH;
            drawW = drawH * aspect;
        }

        Vector2 center = displaySize * 0.5f;
        Vector2 min = center - new Vector2(drawW, drawH) * 0.5f;
        Vector2 max = center + new Vector2(drawW, drawH) * 0.5f;

        drawList.AddImage(texture.Handle, min, max);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.8f, 1f, 0.8f)), 0f, ImDrawFlags.None, 2f);
    }
}
