using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace GposeManager;

public class BrioIntegration : IDisposable
{
    private readonly Plugin plugin;
    private readonly Camera camera;
    private readonly ViewportWindow.ActorInstance actorInstance;
    private readonly List<ViewportWindow.ActorInstance> actorsList;

    private string lastLoadedPath = string.Empty;

    // Attached mode coordinate tracking
    private Vector2 brioWindowPos = Vector2.Zero;
    private Vector2 brioWindowSize = Vector2.Zero;
    private int lastSeenFrame = -1;

    // State tracking for inside the info child pane, browse dialog, and top-level Brio window
    private bool insideLibraryInfoPane = false;
    private bool insideBrioTopWindow = false;
    private bool insideBrioBrowseWindow = false;

    // Native Hooks
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginDelegate(IntPtr name, IntPtr p_open, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginChildStrDelegate(IntPtr str_id, Vector2 size, byte border, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndChildDelegate();

    private Hook<BeginDelegate>? beginHook;
    private Hook<EndDelegate>? endHook;
    private Hook<BeginChildStrDelegate>? beginChildStrHook;
    private Hook<EndChildDelegate>? endChildHook;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public BrioIntegration(Plugin plugin)
    {
        this.plugin = plugin;
        this.camera = new Camera();
        this.camera.Reset();
        this.actorInstance = new ViewportWindow.ActorInstance { Name = "Brio Preview Actor" };
        this.actorsList = new List<ViewportWindow.ActorInstance> { this.actorInstance };

        InitializeHooks();
    }

    private void InitializeHooks()
    {
        try
        {
            var moduleName = "cimgui.dll";
            var moduleHandle = GetModuleHandle(moduleName);
            if (moduleHandle == IntPtr.Zero)
            {
                var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .FirstOrDefault(m => m.ModuleName.Contains("cimgui", System.StringComparison.OrdinalIgnoreCase));
                if (module != null)
                {
                    moduleHandle = module.BaseAddress;
                }
            }

            if (moduleHandle == IntPtr.Zero)
            {
                Plugin.Log.Error("[PM] Could not find cimgui module handle for Brio integration hooks.");
                return;
            }

            var igBeginAddr = GetProcAddress(moduleHandle, "igBegin");
            var igEndAddr = GetProcAddress(moduleHandle, "igEnd");
            var igBeginChildStrAddr = GetProcAddress(moduleHandle, "igBeginChild_Str");
            var igEndChildAddr = GetProcAddress(moduleHandle, "igEndChild");

            if (igBeginAddr != IntPtr.Zero)
            {
                beginHook = Plugin.GameInteropProvider.HookFromAddress<BeginDelegate>(igBeginAddr, BeginDetour);
            }
            if (igEndAddr != IntPtr.Zero)
            {
                endHook = Plugin.GameInteropProvider.HookFromAddress<EndDelegate>(igEndAddr, EndDetour);
            }
            if (igBeginChildStrAddr != IntPtr.Zero)
            {
                beginChildStrHook = Plugin.GameInteropProvider.HookFromAddress<BeginChildStrDelegate>(igBeginChildStrAddr, BeginChildStrDetour);
            }
            if (igEndChildAddr != IntPtr.Zero)
            {
                endChildHook = Plugin.GameInteropProvider.HookFromAddress<EndChildDelegate>(igEndChildAddr, EndChildDetour);
            }

            UpdateHookState();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to initialize Brio integration hooks: {ex.Message}");
        }
    }

    public void UpdateHookState()
    {
        beginHook?.Enable();
        endHook?.Enable();
        beginChildStrHook?.Enable();
        endChildHook?.Enable();
    }

    public void Dispose()
    {
        beginHook?.Dispose();
        beginHook = null;
        endHook?.Dispose();
        endHook = null;
        beginChildStrHook?.Dispose();
        beginChildStrHook = null;
        endChildHook?.Dispose();
        endChildHook = null;
    }

    private byte BeginDetour(IntPtr namePtr, IntPtr p_open, int flags)
    {
        var name = GetUtf8String(namePtr);
        var result = beginHook != null ? beginHook.Original(namePtr, p_open, flags) : (byte)0;

        // Ensure we only capture the top-level parent window, not nested child windows (which contain '/' or child flags)
        bool isChildWindow = (flags & (1 << 24)) != 0 || name.Contains('/');
        if (result != 0 && !isChildWindow)
        {
            if (name.EndsWith("##brio_library_popup", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("###brio_library_window", StringComparison.OrdinalIgnoreCase))
            {
                brioWindowPos = ImGui.GetWindowPos();
                brioWindowSize = ImGui.GetWindowSize();
                lastSeenFrame = (int)ImGui.GetFrameCount();
                insideBrioTopWindow = true;
            }
            else if (name.Contains("###import_browse", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("###export_pose", StringComparison.OrdinalIgnoreCase) ||
                     (name.StartsWith("Import ", StringComparison.OrdinalIgnoreCase) && name.Contains("###")))
            {
                brioWindowPos = ImGui.GetWindowPos();
                brioWindowSize = ImGui.GetWindowSize();
                lastSeenFrame = (int)ImGui.GetFrameCount();
                insideBrioBrowseWindow = true;
            }
        }

        return result;
    }

    private byte BeginChildStrDetour(IntPtr strIdPtr, Vector2 size, byte border, int flags)
    {
        var strId = GetUtf8String(strIdPtr);
        if (strId == "###library_info_pane")
        {
            insideLibraryInfoPane = true;
        }

        return beginChildStrHook != null ? beginChildStrHook.Original(strIdPtr, size, border, flags) : (byte)0;
    }

    private void EndChildDetour()
    {
        try
        {
            if (insideLibraryInfoPane)
            {
                insideLibraryInfoPane = false;

                // Injection Mode: Render mannequin directly inside Brio's info pane
                if (plugin.Configuration.BrioLibraryIntegration && plugin.Configuration.BrioIntegrationMode == 0)
                {
                    if (BrioReflectionHelper.TryGetSelectedPosePath(out var path))
                    {
                        CheckAndLoadPose(path);

                        var availHeight = ImGui.GetContentRegionAvail().Y;
                        if (availHeight >= 120f)
                        {
                            ImGui.Separator();
                            ImGui.Spacing();

                            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "GPose Mannequin Preview");
                            ImGui.SameLine();

                            float cogBtnSize = ImGui.GetTextLineHeight();
                            float rightPos = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - cogBtnSize - 4f;
                            if (rightPos > ImGui.GetCursorPosX())
                            {
                                ImGui.SetCursorPosX(rightPos);
                            }

                            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.15f));
                            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.25f));
                            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
                            ImGui.PushFont(UiBuilder.IconFont);
                            if (ImGui.Button(FontAwesomeIcon.Cog.ToIconString() + "##inject_brio_settings", new Vector2(cogBtnSize + 4f, cogBtnSize + 4f)))
                            {
                                plugin.ToggleConfigUi();
                            }
                            ImGui.PopFont();
                            ImGui.PopStyleVar();
                            ImGui.PopStyleColor(3);
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip("Open Pose Manager Settings");
                            }

                            ImGui.Spacing();

                            var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 5);
                            DrawInteractiveViewport(size);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (ImGui.GetFrameCount() % 3600 == 0)
            {
                Plugin.Log.Error($"[PM] Error in EndChildDetour: {ex}");
            }
        }

        endChildHook?.Original();
    }

    public bool IsBrioWindowActive() => (int)ImGui.GetFrameCount() - lastSeenFrame <= 2;

    private void EndDetour()
    {
        try
        {
            if (insideBrioBrowseWindow)
            {
                insideBrioBrowseWindow = false;

                // Inside "Browse for file" window:
                // Always render an attached preview window snapped to the browse window as long as integration is enabled
                if (plugin.Configuration.BrioLibraryIntegration)
                {
                    if (BrioReflectionHelper.TryGetSelectedPosePathFromFileDialog(out var path) ||
                        BrioReflectionHelper.TryGetSelectedPosePath(out path))
                    {
                        CheckAndLoadPose(path);
                        DrawAttachedPreviewWindow();
                    }
                }

                // Render Settings Window if open during Browse modal
                DrawModalSettingsWindow();
            }
            else if (insideBrioTopWindow)
            {
                insideBrioTopWindow = false;

                bool isModal = BrioReflectionHelper.IsBrioModalOpen();

                // 1. Attached Mode: Render separate snapped window beside Brio
                if (plugin.Configuration.BrioLibraryIntegration && plugin.Configuration.BrioIntegrationMode == 1)
                {
                    if (BrioReflectionHelper.TryGetSelectedPosePath(out var path))
                    {
                        CheckAndLoadPose(path);
                        DrawAttachedPreviewWindow();
                    }
                }

                // 2. Render Settings Window if open during Brio session
                DrawModalSettingsWindow();
            }
        }
        catch (Exception ex)
        {
            if (ImGui.GetFrameCount() % 3600 == 0)
            {
                Plugin.Log.Error($"[PM] Error in EndDetour: {ex}");
            }
        }

        endHook?.Original();
    }

    private void DrawModalSettingsWindow()
    {
        if (!plugin.ConfigWindow.IsOpen) return;

        ImGui.SetNextWindowSize(new Vector2(400, 580), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(400, 300), ImGui.GetIO().DisplaySize);

        bool isOpen = true;
        if (ImGui.Begin("Pose Manager Settings###PoseManagerConfigWindow", ref isOpen, ImGuiWindowFlags.None))
        {
            plugin.ConfigWindow.DrawSettingsBody();
        }
        ImGui.End();

        if (!isOpen)
        {
            plugin.ConfigWindow.IsOpen = false;
        }
    }

    private void DrawAttachedPreviewWindow()
    {
        float displayW = ImGui.GetIO().DisplaySize.X;
        float displayH = ImGui.GetIO().DisplaySize.Y;
        float attachedWidth = 500f;

        // Default to right side of Brio window
        float targetX = brioWindowPos.X + brioWindowSize.X + 5f;

        // If right side goes off-screen, snap to the left side of Brio window
        if (targetX + attachedWidth > displayW && brioWindowPos.X - attachedWidth - 5f >= 0)
        {
            targetX = brioWindowPos.X - attachedWidth - 5f;
        }
        else
        {
            targetX = Math.Clamp(targetX, 0f, Math.Max(0f, displayW - attachedWidth));
        }

        float targetY = Math.Clamp(brioWindowPos.Y, 0f, Math.Max(0f, displayH - brioWindowSize.Y));
        float targetHeight = Math.Min(brioWindowSize.Y, displayH);

        ImGui.SetNextWindowPos(new Vector2(targetX, targetY));
        ImGui.SetNextWindowSize(new Vector2(attachedWidth, targetHeight));

        var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("Pose Preview##brio_preview_attached", flags))
        {
            // Header bar with title & Cog settings button
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "GPose Mannequin Preview");
            ImGui.SameLine();

            float cogBtnSize = ImGui.GetTextLineHeight();
            float rightPos = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - cogBtnSize - 4f;
            if (rightPos > ImGui.GetCursorPosX())
            {
                ImGui.SetCursorPosX(rightPos);
            }

            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.15f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.25f));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button(FontAwesomeIcon.Cog.ToIconString() + "##attach_brio_settings", new Vector2(cogBtnSize + 4f, cogBtnSize + 4f)))
            {
                plugin.ToggleConfigUi();
            }
            ImGui.PopFont();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Open Pose Manager Settings");
            }

            ImGui.Spacing();

            var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 5);
            DrawInteractiveViewport(size);
        }
        ImGui.End();
    }

    private void CheckAndLoadPose(string path)
    {
        if (path == lastLoadedPath) return;

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var pose = JsonSerializer.Deserialize<PoseData>(json);
                if (pose != null)
                {
                    pose.NormalizeBoneAliases();
                    actorInstance.FilePath = path;
                    actorInstance.Pose = pose;

                    // Set initial offsets
                    if (plugin.Configuration.AutoResetOffsetOnLoad)
                    {
                        actorInstance.OffsetPosition = Vector3.Zero;
                        actorInstance.OffsetRotationEuler = Vector3.Zero;
                    }
                    else
                    {
                        var diffPos = pose.ModelDifference.Position;
                        var diffRot = pose.ModelDifference.Rotation;
                        var euler = QuaternionToEuler(diffRot);

                        actorInstance.OffsetPosition = diffPos;
                        actorInstance.OffsetRotationEuler = euler * (float)(180.0 / Math.PI);
                    }

                    // Auto-Frame or Auto-Focus Brio Preview Camera
                    if (plugin.Configuration.AutoFramePoseOnLoad)
                    {
                        Vector3 pelvisPos = pose.GetRootOrPelvisPosition();

                        Vector3 minBound = new Vector3(float.MaxValue);
                        Vector3 maxBound = new Vector3(float.MinValue);
                        foreach (var bone in pose.Bones.Values)
                        {
                            var localPos = bone.Position - pelvisPos;
                            minBound = Vector3.Min(minBound, localPos);
                            maxBound = Vector3.Max(maxBound, localPos);
                        }

                        if (pose.Bones.Count > 0)
                        {
                            Vector3 center = (minBound + maxBound) * 0.5f;

                            float radius = 0.1f;
                            foreach (var bone in pose.Bones.Values)
                            {
                                var localPos = bone.Position - pelvisPos;
                                float dist = Vector3.Distance(localPos, center);
                                if (dist > radius) radius = dist;
                            }

                            float halfFovRad = (float)(camera.Fov * 0.5f * Math.PI / 180.0);
                            float tanHalfFov = (float)Math.Tan(halfFovRad);
                            float targetZoom = (radius * 1.25f) / tanHalfFov;

                            camera.Target = actorInstance.OffsetPosition + center;
                            camera.Zoom = Math.Clamp(targetZoom, 1.0f, 6.0f);
                        }
                    }
                    else if (plugin.Configuration.AutoFocusCameraOnLoad)
                    {
                        camera.Target = actorInstance.OffsetPosition + new Vector3(0, 0.7f, 0);
                    }
                    
                    lastLoadedPath = path;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to load brio preview pose from {path}: {ex.Message}");
        }
    }

    private void DrawInteractiveViewport(Vector2 size)
    {
        if (size.X < 20f || size.Y < 20f) return;

        var canvasStart = ImGui.GetCursorScreenPos();
        
        ImGui.InvisibleButton("brio_mannequin_canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool isHovered = ImGui.IsItemHovered();
        bool isActive = ImGui.IsItemActive();

        float sensitivity = plugin.Configuration.CameraSensitivity;

        if (isActive)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                camera.Yaw -= delta.X * 0.007f * sensitivity;
                camera.Pitch = Math.Clamp(camera.Pitch + delta.Y * 0.007f * sensitivity, -1.45f, 1.45f);
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
            }
            else if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
            {
                var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Right);
                float cosYaw = (float)Math.Cos(camera.Yaw);
                float sinYaw = (float)Math.Sin(camera.Yaw);
                var right = new Vector3(cosYaw, 0, -sinYaw);
                camera.Target += (right * (-delta.X * 0.0012f * camera.Zoom) + Vector3.UnitY * (delta.Y * 0.0012f * camera.Zoom)) * sensitivity;
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Right);
            }
        }

        if (isHovered)
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0.0f)
            {
                camera.Zoom = Math.Clamp(camera.Zoom - wheel * 0.2f, 0.5f, 15.0f);
            }
        }

        camera.CanvasStart = canvasStart;
        camera.CanvasSize = size;

        var drawList = ImGui.GetWindowDrawList();

        // Canvas Background
        drawList.AddRectFilled(canvasStart, canvasStart + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.1f, 1.0f)));

        // Configure mannequin drawing based on scale
        var configCopy = new Configuration
        {
            ShowGrid = plugin.Configuration.ShowGrid,
            EnableDepthShading = plugin.Configuration.EnableDepthShading,
            DepthFadeIntensity = plugin.Configuration.DepthFadeIntensity,
            SkeletonColor = plugin.Configuration.SkeletonColor,
            JointSize = plugin.Configuration.JointSize * plugin.Configuration.BrioMannequinScale,
            LimbThickness = plugin.Configuration.LimbThickness * plugin.Configuration.BrioMannequinScale
        };

        // Render mannequin
        SkeletonRenderer.Render(drawList, camera, configCopy, actorsList);

        // Draw Canvas Border
        drawList.AddRect(canvasStart, canvasStart + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.25f, 0.8f)), 4.0f, ImDrawFlags.None, 1.5f);
    }

    private static Vector3 QuaternionToEuler(Quaternion q)
    {
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

    private static string GetUtf8String(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;
        try
        {
            int len = 0;
            const int maxLen = 512;
            while (len < maxLen && Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }
        catch
        {
            return string.Empty;
        }
    }
}
