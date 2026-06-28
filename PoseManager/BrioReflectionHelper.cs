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
    private static bool failed = false;

    public class BrioActorTarget
    {
        public string Name { get; set; } = string.Empty;
        public object EntityObj { get; set; } = null!;
        public object PosingCapObj { get; set; } = null!;
    }

    public static void Initialize()
    {
        if (initialized || failed) return;

        try
        {
            brioAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Brio");
            if (brioAssembly == null) return; // Brio not loaded yet

            brioType = brioAssembly.GetType("Brio.Brio");
            if (brioType == null)
            {
                Plugin.Log.Warning("[PM] Brio.Brio type not found.");
                failed = true;
                return;
            }

            servicesField = brioType.GetField("_services", BindingFlags.NonPublic | BindingFlags.Static);
            if (servicesField == null)
            {
                Plugin.Log.Warning("[PM] Brio._services static field not found.");
                failed = true;
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

            initialized = true;
            Plugin.Log.Information("[PM] Brio selection reflection initialized successfully.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[PM] Failed to initialize Brio selection reflection: {ex}");
            failed = true;
        }
    }

    public static List<BrioActorTarget> GetBrioActors()
    {
        var list = new List<BrioActorTarget>();
        Initialize();
        if (!initialized || failed || servicesField == null || entityManagerType == null || entityMapField == null)
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

    public static bool TryGetSelectedPosePath(out string path)
    {
        path = string.Empty;
        Initialize();
        if (!initialized || failed || servicesField == null || uiManagerType == null || libraryWindowField == null) return false;

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

            // Check if it is modal
            bool isModal = isModalField != null && (bool)isModalField.GetValue(libraryWindow)!;
            if (!isModal) return false;

            // Check if modal filter is Poses
            if (modalFilterField != null && filterNameProp != null)
            {
                var filter = modalFilterField.GetValue(libraryWindow);
                if (filter == null) return false;
                var filterName = filterNameProp.GetValue(filter) as string;
                if (filterName != "Poses") return false;
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
}
