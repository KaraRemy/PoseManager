using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Dalamud.Bindings.ImGui;

namespace GposeManager;

public static class BrioReflectionHelper
{
    private static Assembly? brioAssembly;
    private static Type? brioType;
    private static FieldInfo? servicesField;
    private static Type? uiManagerType;
    private static FieldInfo? libraryWindowField;
    private static Type? libraryWindowType;
    private static FieldInfo? isModalField;
    private static FieldInfo? modalFilterField;
    private static FieldInfo? selectedField;
    private static Type? fileEntryType;
    private static FieldInfo? filePathField;
    private static PropertyInfo? filterNameProp;

    private static Type? entityManagerType;
    private static FieldInfo? entityMapField;
    private static PropertyInfo? actorFriendlyNameProp;
    private static PropertyInfo? actorCapabilitiesProp;

    private static bool initialized = false;

    public class BrioActorTarget
    {
        public string Name { get; set; } = string.Empty;
        public object EntityObj { get; set; } = null!;
        public object PosingCapObj { get; set; } = null!;
    }

    public static void Initialize()
    {
        if (initialized) return;

        try
        {
            brioAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Brio");
            if (brioAssembly == null) return; // Brio not loaded yet

            brioType = brioAssembly.GetType("Brio.Brio");
            if (brioType == null)
            {
                return;
            }

            servicesField = brioType.GetField("_services", BindingFlags.NonPublic | BindingFlags.Static);
            if (servicesField == null)
            {
                return;
            }

            uiManagerType = brioAssembly.GetType("Brio.UI.UIManager");
            libraryWindowType = brioAssembly.GetType("Brio.UI.Windows.LibraryWindow");
            if (uiManagerType != null && libraryWindowType != null)
            {
                libraryWindowField = uiManagerType.GetField("_libraryWindow", BindingFlags.NonPublic | BindingFlags.Instance);
                isModalField = libraryWindowType.GetField("_isModal", BindingFlags.NonPublic | BindingFlags.Instance);
                modalFilterField = libraryWindowType.GetField("_modalFilter", BindingFlags.NonPublic | BindingFlags.Instance);
                selectedField = libraryWindowType.GetField("_selected", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            fileEntryType = brioAssembly.GetType("Brio.Library.Sources.FileEntry");
            if (fileEntryType != null)
            {
                filePathField = fileEntryType.GetField("FilePath", BindingFlags.Public | BindingFlags.Instance);
            }

            var filterBaseType = brioAssembly.GetType("Brio.Library.Filters.FilterBase");
            if (filterBaseType != null)
            {
                filterNameProp = filterBaseType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            }

            entityManagerType = brioAssembly.GetType("Brio.Entities.EntityManager");
            if (entityManagerType != null)
            {
                entityMapField = entityManagerType.GetField("_entityMap", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            var actorEntityType = brioAssembly.GetType("Brio.Entities.ActorEntity");
            if (actorEntityType != null)
            {
                actorFriendlyNameProp = actorEntityType.GetProperty("FriendlyName", BindingFlags.Public | BindingFlags.Instance);
                actorCapabilitiesProp = actorEntityType.GetProperty("Capabilities", BindingFlags.Public | BindingFlags.Instance);
            }

            if (servicesField != null && entityManagerType != null && entityMapField != null)
            {
                initialized = true;
                Plugin.Log.Information("[PM] Brio selection reflection initialized successfully.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[PM] Brio selection reflection initialization deferred: {ex.Message}");
        }
    }

    public static List<BrioActorTarget> GetBrioActors()
    {
        var list = new List<BrioActorTarget>();
        Initialize();
        if (!initialized || servicesField == null || entityManagerType == null || entityMapField == null)
            return list;

        try
        {
            var services = servicesField.GetValue(null) as IServiceProvider;
            if (services == null) return list;

            var entityManager = services.GetService(entityManagerType);
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
            Plugin.Log.Error($"[PM] Error getting Brio actors: {ex.Message}");
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
        if (!initialized || servicesField == null || uiManagerType == null || libraryWindowField == null) return false;

        try
        {
            var services = servicesField.GetValue(null) as IServiceProvider;
            if (services == null) return false;

            var uiManager = services.GetService(uiManagerType!);
            if (uiManager == null) return false;

            var libraryWindow = libraryWindowField!.GetValue(uiManager);
            if (libraryWindow == null) return false;

            var isOpenProp = libraryWindow.GetType().GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance);
            if (isOpenProp == null) return false;
            bool isOpen = (bool)isOpenProp.GetValue(libraryWindow)!;
            if (!isOpen) return false;

            return isModalField != null && (bool)isModalField.GetValue(libraryWindow)!;
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
        if (!initialized || servicesField == null || uiManagerType == null || libraryWindowField == null) return false;

        try
        {
            var services = servicesField.GetValue(null) as IServiceProvider;
            if (services == null) return false;

            var uiManager = services.GetService(uiManagerType!);
            if (uiManager == null) return false;

            var libraryWindow = libraryWindowField!.GetValue(uiManager);
            if (libraryWindow == null) return false;

            // Check if window is open
            var isOpenProp = libraryWindow.GetType().GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance);
            if (isOpenProp == null) return false;
            bool isOpen = (bool)isOpenProp.GetValue(libraryWindow)!;
            if (!isOpen) return false;

            // If in modal mode, check if modal filter is Poses
            isModal = isModalField != null && (bool)isModalField.GetValue(libraryWindow)!;
            if (isModal)
            {
                if (modalFilterField != null && filterNameProp != null)
                {
                    var filter = modalFilterField.GetValue(libraryWindow);
                    if (filter == null) return false;
                    var filterName = filterNameProp.GetValue(filter) as string;
                    if (filterName != "Poses") return false;
                }
            }

            // Get selected entry
            if (selectedField != null)
            {
                var selected = selectedField.GetValue(libraryWindow);
                if (selected != null && fileEntryType != null && fileEntryType.IsInstanceOfType(selected))
                {
                    if (filePathField != null)
                    {
                        var filePath = filePathField.GetValue(selected) as string;
                        if (!string.IsNullOrEmpty(filePath) && filePath.EndsWith(".pose", StringComparison.OrdinalIgnoreCase))
                        {
                            path = filePath;
                            return true;
                        }
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

    private static FieldInfo? fileDialogManagerField;

    public static bool TryGetSelectedPosePathFromFileDialog(out string path)
    {
        path = string.Empty;
        Initialize();
        if (!initialized || servicesField == null || uiManagerType == null) return false;

        try
        {
            var services = servicesField.GetValue(null) as IServiceProvider;
            if (services == null) return false;

            var uiManager = services.GetService(uiManagerType!);
            if (uiManager == null) return false;

            if (fileDialogManagerField == null)
            {
                fileDialogManagerField = uiManagerType.GetField("FileDialogManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (fileDialogManagerField == null) return false;
            var fdm = fileDialogManagerField.GetValue(uiManager);
            if (fdm == null) return false;

            // In FileDialogManager, find dialog field
            var dialogField = fdm.GetType().GetField("dialog", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (dialogField == null) return false;
            var dialog = dialogField.GetValue(fdm);
            if (dialog == null) return false;

            var dialogType = dialog.GetType();

            // Try methods: GetFilePathName(), GetFilePathNameWithExt(), GetSelected()
            var getFilePathNameMethod = dialogType.GetMethod("GetFilePathName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getFilePathNameMethod != null)
            {
                var res = getFilePathNameMethod.Invoke(dialog, null) as string;
                if (!string.IsNullOrEmpty(res) && res.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) && File.Exists(res))
                {
                    path = res;
                    return true;
                }
            }

            // Try properties / fields: CurrentPath + SelectedFileName / fileNameBuffer
            string? currentDir = null;
            var getCurrentPathMethod = dialogType.GetMethod("GetCurrentPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getCurrentPathMethod != null)
            {
                currentDir = getCurrentPathMethod.Invoke(dialog, null) as string;
            }
            if (string.IsNullOrEmpty(currentDir))
            {
                var curPathProp = dialogType.GetProperty("CurrentPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                                  dialogType.GetProperty("Path", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (curPathProp != null)
                {
                    currentDir = curPathProp.GetValue(dialog) as string;
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

            if (!string.IsNullOrEmpty(selectedFile) && selectedFile.EndsWith(".pose", StringComparison.OrdinalIgnoreCase))
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
                    if (!string.IsNullOrEmpty(first) && first.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) && File.Exists(first))
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
                    if (!string.IsNullOrEmpty(val) && val.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) && File.Exists(val))
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
