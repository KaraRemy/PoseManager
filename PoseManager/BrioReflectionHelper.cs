using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;

namespace GposeManager;

public static class BrioReflectionHelper
{
    private static Assembly? brioAssembly;
    private static Type? brioType;
    private static FieldInfo? servicesField;
    private static Type? uiManagerType;
    private static PropertyInfo? uiManagerInstanceProp;
    private static FieldInfo? libraryWindowField;
    private static Type? libraryWindowType;
    private static Type? modalManagerType;
    private static PropertyInfo? modalManagerInstanceProp;
    private static FieldInfo? modalLibraryWindowField;
    private static FieldInfo? isModalField;
    private static PropertyInfo? isModalProp;
    private static FieldInfo? modalFilterField;
    private static FieldInfo? selectedField;
    private static FieldInfo? libraryEntityManagerField;
    private static Type? fileEntryType;
    private static FieldInfo? filePathField;
    private static FieldInfo? filterNameField;
    private static PropertyInfo? filterNameProp;

    private static Type? entityManagerType;
    private static FieldInfo? entityMapField;
    private static PropertyInfo? actorFriendlyNameProp;
    private static PropertyInfo? actorCapabilitiesProp;
    private static FieldInfo? fileDialogManagerField;

    private static bool initialized = false;

    public class BrioActorTarget
    {
        public string Name { get; set; } = string.Empty;
        public object EntityObj { get; set; } = null!;
        public object PosingCapObj { get; set; } = null!;
    }

    public static bool IsSupportedPoseFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetFilterName(object? filter)
    {
        if (filter == null) return null;
        try
        {
            var name = (filterNameField?.GetValue(filter) ?? filterNameProp?.GetValue(filter)) as string;
            if (string.IsNullOrEmpty(name))
            {
                name = (filter.GetType().GetField("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(filter) ??
                        filter.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(filter)) as string;
            }
            return name;
        }
        catch
        {
            return null;
        }
    }

    public static void Initialize(bool force = false)
    {
        if (initialized && !force) return;

        try
        {
            var brioAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "Brio")
                .Reverse()
                .ToList();

            if (brioAssemblies.Count == 0) return;

            // Find active assembly by checking for non-null UIManager.Instance or ModalManager.Instance
            Assembly? activeAsm = null;
            foreach (var asm in brioAssemblies)
            {
                var uiType = asm.GetType("Brio.UI.UIManager");
                var instProp = uiType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instProp?.GetValue(null) != null)
                {
                    activeAsm = asm;
                    break;
                }
            }

            brioAssembly = activeAsm ?? brioAssemblies.FirstOrDefault();
            if (brioAssembly == null) return;

            brioType = brioAssembly.GetType("Brio.Brio");
            servicesField = brioType?.GetField("_services", BindingFlags.NonPublic | BindingFlags.Static);

            uiManagerType = brioAssembly.GetType("Brio.UI.UIManager");
            uiManagerInstanceProp = uiManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);

            modalManagerType = brioAssembly.GetType("Brio.UI.ModalManager");
            modalManagerInstanceProp = modalManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (modalManagerType != null)
            {
                modalLibraryWindowField = modalManagerType.GetField("_libraryWindow", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            libraryWindowType = brioAssembly.GetType("Brio.UI.Windows.LibraryWindow");
            if (libraryWindowType != null)
            {
                isModalField = libraryWindowType.GetField("_isModal", BindingFlags.NonPublic | BindingFlags.Instance);
                isModalProp = libraryWindowType.GetProperty("IsModal", BindingFlags.Public | BindingFlags.Instance);
                modalFilterField = libraryWindowType.GetField("_modalFilter", BindingFlags.NonPublic | BindingFlags.Instance);
                selectedField = libraryWindowType.GetField("_selected", BindingFlags.NonPublic | BindingFlags.Instance);
                libraryEntityManagerField = libraryWindowType.GetField("_entityManager", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (uiManagerType != null)
            {
                libraryWindowField = uiManagerType.GetField("_libraryWindow", BindingFlags.NonPublic | BindingFlags.Instance);
                fileDialogManagerField = uiManagerType.GetField("FileDialogManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            fileEntryType = brioAssembly.GetType("Brio.Library.Sources.FileEntry");
            if (fileEntryType != null)
            {
                filePathField = fileEntryType.GetField("FilePath", BindingFlags.Public | BindingFlags.Instance);
            }

            var filterBaseType = brioAssembly.GetType("Brio.Library.Filters.FilterBase");
            if (filterBaseType != null)
            {
                filterNameField = filterBaseType.GetField("Name", BindingFlags.Public | BindingFlags.Instance);
                filterNameProp = filterBaseType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            }

            entityManagerType = brioAssembly.GetType("Brio.Entities.EntityManager");
            if (entityManagerType != null)
            {
                entityMapField = entityManagerType.GetField("_entityMap", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            var actorEntityType = brioAssembly.GetType("Brio.Entities.Actor.ActorEntity") ?? brioAssembly.GetType("Brio.Entities.ActorEntity");
            if (actorEntityType != null)
            {
                actorFriendlyNameProp = actorEntityType.GetProperty("FriendlyName", BindingFlags.Public | BindingFlags.Instance);
                actorCapabilitiesProp = actorEntityType.GetProperty("Capabilities", BindingFlags.Public | BindingFlags.Instance);
            }

            initialized = (uiManagerType != null && libraryWindowType != null);
            if (initialized)
            {
                Plugin.Log.Information("[PM] Brio selection reflection initialized successfully.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[PM] Brio selection reflection initialization deferred: {ex.Message}");
        }
    }

    public static object? GetUIManager()
    {
        Initialize();
        try
        {
            var inst = uiManagerInstanceProp?.GetValue(null);
            if (inst != null) return inst;
        }
        catch { }

        try
        {
            Initialize(force: true);
            return uiManagerInstanceProp?.GetValue(null);
        }
        catch { }

        return null;
    }

    public static object? GetLibraryWindow()
    {
        Initialize();

        // 1. Try via UIManager.Instance
        try
        {
            var uiManager = GetUIManager();
            if (uiManager != null && libraryWindowField != null)
            {
                var lw = libraryWindowField.GetValue(uiManager);
                if (lw != null) return lw;
            }
        }
        catch { }

        // 2. Try via ModalManager.Instance
        try
        {
            if (modalManagerInstanceProp != null && modalLibraryWindowField != null)
            {
                var modalManager = modalManagerInstanceProp.GetValue(null);
                if (modalManager != null)
                {
                    var lw = modalLibraryWindowField.GetValue(modalManager);
                    if (lw != null) return lw;
                }
            }
        }
        catch { }

        // 3. Fallback to IServiceProvider safely catching ObjectDisposedException
        try
        {
            if (servicesField != null && libraryWindowType != null)
            {
                var services = servicesField.GetValue(null) as IServiceProvider;
                if (services != null)
                {
                    return services.GetService(libraryWindowType);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch { }

        return null;
    }

    public static object? GetEntityManager()
    {
        Initialize();

        // 1. Try from LibraryWindow
        try
        {
            var lw = GetLibraryWindow();
            if (lw != null && libraryEntityManagerField != null)
            {
                var em = libraryEntityManagerField.GetValue(lw);
                if (em != null) return em;
            }
        }
        catch { }

        // 2. Try from UIManager._overlayWindow._entityManager
        try
        {
            var uiManager = GetUIManager();
            if (uiManager != null && uiManagerType != null)
            {
                var overlayWinField = uiManagerType.GetField("_overlayWindow", BindingFlags.NonPublic | BindingFlags.Instance);
                var overlayWin = overlayWinField?.GetValue(uiManager);
                if (overlayWin != null)
                {
                    var emField = overlayWin.GetType().GetField("_entityManager", BindingFlags.NonPublic | BindingFlags.Instance);
                    var em = emField?.GetValue(overlayWin);
                    if (em != null) return em;
                }
            }
        }
        catch { }

        // 3. Fallback via ServiceProvider with ObjectDisposedException caught
        try
        {
            if (servicesField != null && entityManagerType != null)
            {
                var services = servicesField.GetValue(null) as IServiceProvider;
                if (services != null)
                {
                    return services.GetService(entityManagerType);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch { }

        return null;
    }

    public static List<BrioActorTarget> GetBrioActors()
    {
        var list = new List<BrioActorTarget>();
        Initialize();
        if (entityManagerType == null || entityMapField == null)
            return list;

        try
        {
            var entityManager = GetEntityManager();
            if (entityManager == null) return list;

            var entityMap = entityMapField.GetValue(entityManager) as IDictionary;
            if (entityMap == null) return list;

            foreach (var valueObj in entityMap.Values)
            {
                if (valueObj == null) continue;
                var type = valueObj.GetType();
                if (type.Name == "ActorEntity" || type.BaseType?.Name == "ActorEntity")
                {
                    var friendlyNameProp = actorFriendlyNameProp ?? type.GetProperty("FriendlyName", BindingFlags.Public | BindingFlags.Instance);
                    string name = friendlyNameProp?.GetValue(valueObj) as string ?? "Actor";

                    var capabilitiesProp = actorCapabilitiesProp ?? type.GetProperty("Capabilities", BindingFlags.Public | BindingFlags.Instance);
                    var capsList = capabilitiesProp?.GetValue(valueObj) as IEnumerable;
                    object? posingCap = null;
                    if (capsList != null)
                    {
                        foreach (var cap in capsList)
                        {
                            if (cap != null && cap.GetType().Name == "PosingCapability")
                            {
                                posingCap = cap;
                                break;
                            }
                        }
                    }

                    if (posingCap != null)
                    {
                        list.Add(new BrioActorTarget
                        {
                            Name = name,
                            EntityObj = valueObj,
                            PosingCapObj = posingCap
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (ImGui.GetFrameCount() % 3600 == 0)
            {
                Plugin.Log.Error($"[PM] Error getting Brio actors: {ex.Message}");
            }
        }

        return list;
    }

    public static bool ApplyPoseToBrioActor(object posingCapObj, string poseFilePath, int importMode)
    {
        Initialize();
        if (brioAssembly == null || posingCapObj == null || string.IsNullOrEmpty(poseFilePath) || !File.Exists(poseFilePath)) return false;

        if (poseFilePath.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.Warning("[PM] Direct .cmp pose application to Brio actor is disabled.");
            return false;
        }

        try
        {
            Type poseFileType = brioAssembly.GetType("Brio.Files.PoseFile")!;
            Type cmToolPoseFileType = brioAssembly.GetType("Brio.Files.CMToolPoseFile")!;

            bool isCmp = poseFilePath.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase);
            string jsonText = File.ReadAllText(poseFilePath);

            Type brioSerializerType = brioAssembly.GetType("Brio.Core.JsonSerializer")!;
            MethodInfo genericDeserializeMethod = brioSerializerType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!;
            MethodInfo deserializeMethod = genericDeserializeMethod.MakeGenericMethod(isCmp ? cmToolPoseFileType : poseFileType);
            object? poseFileObj = deserializeMethod.Invoke(null, new object[] { jsonText });
            if (poseFileObj == null) return false;

            Type posingCapType = posingCapObj.GetType();
            var methods = posingCapType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "ImportPose" && m.GetParameters().Length >= 6)
                .ToList();

            MethodInfo? importMethod = methods.FirstOrDefault();
            if (importMethod == null) return false;

            Type oneOfType = importMethod.GetParameters()[0].ParameterType;
            MethodInfo fromMethod = isCmp 
                ? oneOfType.GetMethod("FromT1", BindingFlags.Public | BindingFlags.Static)!
                : oneOfType.GetMethod("FromT0", BindingFlags.Public | BindingFlags.Static)!;
            object oneOfObj = fromMethod.Invoke(null, new object[] { poseFileObj })!;

            if (importMethod != null)
            {
                bool asExpression = (importMode == 1);
                bool asBody = (importMode == 2);

                var paramsInfo = importMethod.GetParameters();
                object?[] args = new object?[paramsInfo.Length];
                args[0] = oneOfObj; // rawPoseFile
                args[1] = null;      // options
                args[2] = asExpression; // asExpression
                args[3] = false;     // asScene
                args[4] = false;     // asIPCpose
                args[5] = asBody;       // asBody
                for (int i = 6; i < paramsInfo.Length; i++)
                {
                    args[i] = paramsInfo[i].HasDefaultValue ? paramsInfo[i].DefaultValue : null;
                }

                importMethod.Invoke(posingCapObj, args);
                Plugin.Log.Information($"[PM] Applied pose to Brio actor (mode: {importMode})");
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to apply pose via reflection: {ex}");
        }

        return false;
    }

    public static bool IsBrioModalOpen()
    {
        Initialize();
        try
        {
            var libraryWindow = GetLibraryWindow();
            if (libraryWindow == null) return false;

            var isOpenProp = libraryWindow.GetType().GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance);
            if (isOpenProp == null) return false;
            bool isOpen = (bool)isOpenProp.GetValue(libraryWindow)!;
            if (!isOpen) return false;

            if (isModalProp != null)
                return (bool)isModalProp.GetValue(libraryWindow)!;
            if (isModalField != null)
                return (bool)isModalField.GetValue(libraryWindow)!;

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetSelectedPosePath(out string path) => TryGetSelectedPosePath(out path, out _);

    public static bool TryGetSelectedPosePath(out string path, out bool isModal)
    {
        path = string.Empty;
        isModal = false;
        Initialize();

        try
        {
            var libraryWindow = GetLibraryWindow();
            if (libraryWindow == null) return false;

            // Check if window is open
            var isOpenProp = libraryWindow.GetType().GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance);
            if (isOpenProp == null) return false;
            bool isOpen = (bool)isOpenProp.GetValue(libraryWindow)!;
            if (!isOpen) return false;

            // If in modal mode, check if modal filter is Poses
            isModal = (isModalProp != null ? (bool)isModalProp.GetValue(libraryWindow)! : (isModalField != null && (bool)isModalField.GetValue(libraryWindow)!));
            if (isModal && modalFilterField != null)
            {
                var filter = modalFilterField.GetValue(libraryWindow);
                if (filter != null)
                {
                    var filterName = GetFilterName(filter);
                    if (!string.IsNullOrEmpty(filterName) && !filterName.Equals("Poses", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            // Get selected entry
            if (selectedField != null)
            {
                var selected = selectedField.GetValue(libraryWindow);
                if (selected != null)
                {
                    string? filePath = null;
                    if (fileEntryType != null && fileEntryType.IsInstanceOfType(selected) && filePathField != null)
                    {
                        filePath = filePathField.GetValue(selected) as string;
                    }
                    else
                    {
                        filePath = (selected.GetType().GetField("FilePath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(selected) ??
                                    selected.GetType().GetProperty("FilePath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(selected)) as string;
                    }

                    if (!string.IsNullOrEmpty(filePath) && IsSupportedPoseFile(filePath))
                    {
                        path = filePath;
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (ImGui.GetFrameCount() % 3600 == 0)
            {
                Plugin.Log.Warning($"[PM] Reflection error in TryGetSelectedPosePath: {ex.Message}");
            }
        }

        return false;
    }

    public static bool TryGetSelectedPosePathFromFileDialog(out string path)
    {
        path = string.Empty;
        Initialize();

        try
        {
            object? fdm = null;
            if (fileDialogManagerField != null)
            {
                var uiManager = GetUIManager();
                if (uiManager != null)
                {
                    fdm = fileDialogManagerField.GetValue(uiManager);
                }
            }

            if (fdm == null) return false;

            var dialogField = fdm.GetType().GetField("dialog", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (dialogField == null) return false;
            var dialog = dialogField.GetValue(fdm);
            if (dialog == null) return false;

            var dialogType = dialog.GetType();

            var getFilePathNameMethod = dialogType.GetMethod("GetFilePathName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getFilePathNameMethod != null)
            {
                var res = getFilePathNameMethod.Invoke(dialog, null) as string;
                if (!string.IsNullOrEmpty(res) && IsSupportedPoseFile(res) && File.Exists(res))
                {
                    path = res;
                    return true;
                }
            }

            string? currentDir = null;
            var getCurrentPathMethod = dialogType.GetMethod("GetCurrentPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getCurrentPathMethod != null)
            {
                currentDir = getCurrentPathMethod.Invoke(dialog, null) as string;
            }
            if (string.IsNullOrEmpty(currentDir))
            {
                currentDir = dialogType.GetField("CurrentPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dialog) as string ??
                             dialogType.GetProperty("CurrentPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dialog) as string ??
                             dialogType.GetProperty("Path", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dialog) as string;
            }

            var getSelectedMethod = dialogType.GetMethod("GetSelected", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getSelectedMethod != null)
            {
                var res = getSelectedMethod.Invoke(dialog, null) as string;
                if (!string.IsNullOrEmpty(res) && !string.IsNullOrEmpty(currentDir))
                {
                    var combined = Path.Combine(currentDir, res);
                    if (IsSupportedPoseFile(combined) && File.Exists(combined))
                    {
                        path = combined;
                        return true;
                    }
                }
            }

            string? selectedFile = null;
            var getFileNameMethod = dialogType.GetMethod("GetFileName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getFileNameMethod != null)
            {
                selectedFile = getFileNameMethod.Invoke(dialog, null) as string;
            }

            if (string.IsNullOrEmpty(selectedFile))
            {
                var fileNameField = dialogType.GetField("selectedFileName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                                    dialogType.GetField("fileName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                                    dialogType.GetField("fileNameBuffer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                                    dialogType.GetField("SelectedFileName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fileNameField != null)
                {
                    selectedFile = fileNameField.GetValue(dialog) as string;
                }
            }

            if (!string.IsNullOrEmpty(selectedFile) && IsSupportedPoseFile(selectedFile))
            {
                if (!string.IsNullOrEmpty(currentDir))
                {
                    var combined = Path.Combine(currentDir, selectedFile);
                    if (File.Exists(combined))
                    {
                        path = combined;
                        return true;
                    }
                }
                if (File.Exists(selectedFile))
                {
                    path = selectedFile;
                    return true;
                }
            }

            // Try SelectedFiles / selectedFiles (collection)
            var selFilesProp = dialogType.GetProperty("SelectedFiles", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                               dialogType.GetProperty("Selections", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (selFilesProp != null)
            {
                var filesObj = selFilesProp.GetValue(dialog);
                if (filesObj is IEnumerable<string> list && list.Any())
                {
                    var first = list.First();
                    if (!string.IsNullOrEmpty(first) && IsSupportedPoseFile(first) && File.Exists(first))
                    {
                        path = first;
                        return true;
                    }
                }
            }

            // Try inspecting all string fields of dialog
            foreach (var f in dialogType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.FieldType == typeof(string))
                {
                    var val = f.GetValue(dialog) as string;
                    if (!string.IsNullOrEmpty(val) && IsSupportedPoseFile(val) && File.Exists(val))
                    {
                        path = val;
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (ImGui.GetFrameCount() % 3600 == 0)
            {
                Plugin.Log.Debug($"[PM] TryGetSelectedPosePathFromFileDialog error: {ex.Message}");
            }
        }

        return false;
    }
}
