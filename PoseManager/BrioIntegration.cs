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

    // State tracking for inside the info child pane
    private bool insideLibraryInfoPane = false;

    // Native Hooks
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginDelegate(IntPtr name, IntPtr p_open, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginChildStrDelegate(IntPtr str_id, Vector2 size, byte border, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndChildDelegate();

    private Hook<BeginDelegate>? beginHook;
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
            var igBeginChildStrAddr = GetProcAddress(moduleHandle, "igBeginChild_Str");
            var igEndChildAddr = GetProcAddress(moduleHandle, "igEndChild");

            if (igBeginAddr != IntPtr.Zero)
            {
                beginHook = Plugin.GameInteropProvider.HookFromAddress<BeginDelegate>(igBeginAddr, BeginDetour);
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
        if (plugin.Configuration.BrioLibraryIntegration)
        {
            beginHook?.Enable();
            beginChildStrHook?.Enable();
            endChildHook?.Enable();
        }
        else
        {
            beginHook?.Disable();
            beginChildStrHook?.Disable();
            endChildHook?.Disable();
        }
    }

    public void Dispose()
    {
        beginHook?.Dispose();
        beginHook = null;
        beginChildStrHook?.Dispose();
        beginChildStrHook = null;
        endChildHook?.Dispose();
        endChildHook = null;
    }

    private byte BeginDetour(IntPtr namePtr, IntPtr p_open, int flags)
    {
        var name = GetUtf8String(namePtr);
        var result = beginHook != null ? beginHook.Original(namePtr, p_open, flags) : (byte)0;

        if (result != 0 && name == "Import Poses##brio_library_popup")
        {
            brioWindowPos = ImGui.GetWindowPos();
            brioWindowSize = ImGui.GetWindowSize();
            lastSeenFrame = (int)ImGui.GetFrameCount();
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

                if (plugin.Configuration.BrioLibraryIntegration)
                {
                    if (BrioReflectionHelper.TryGetSelectedPosePath(out var path))
                    {
                        CheckAndLoadPose(path);

                        if (plugin.Configuration.BrioIntegrationMode == 0)
                        {
                            // Injection Mode: Render mannequin directly inside Brio's info pane
                            var availHeight = ImGui.GetContentRegionAvail().Y;
                            if (availHeight >= 120f)
                            {
                                ImGui.Separator();
                                ImGui.Spacing();
                                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.9f, 1.0f), "GPose Mannequin Preview");
                                ImGui.Spacing();

                                var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - 5);
                                DrawInteractiveViewport(size);
                            }
                        }
                        else if (plugin.Configuration.BrioIntegrationMode == 1 && (int)ImGui.GetFrameCount() - lastSeenFrame <= 2)
                        {
                            // Attached Mode: Render separate snapped window inside modal context so it is interactive
                            ImGui.SetNextWindowPos(new Vector2(brioWindowPos.X + brioWindowSize.X + 5, brioWindowPos.Y));
                            ImGui.SetNextWindowSize(new Vector2(500, brioWindowSize.Y));

                            var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar;
                            if (ImGui.Begin("Pose Preview##brio_preview_attached", flags))
                            {
                                DrawInteractiveViewport(ImGui.GetContentRegionAvail());
                            }
                            ImGui.End();
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
                    actorInstance.FilePath = path;
                    actorInstance.Pose = pose;

                    // Set initial offsets
                    var diffPos = pose.ModelDifference.Position;
                    var diffRot = pose.ModelDifference.Rotation;
                    var euler = QuaternionToEuler(diffRot);

                    actorInstance.OffsetPosition = diffPos;
                    actorInstance.OffsetRotationEuler = euler * (float)(180.0 / Math.PI);
                    
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
        int len = 0;
        while (Marshal.ReadByte(ptr, len) != 0) len++;
        byte[] buffer = new byte[len];
        Marshal.Copy(ptr, buffer, 0, len);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }
}
