using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GposeManager;

public class PoseData : IJsonOnDeserialized
{
    [JsonPropertyName("Author")]
    public string? Author { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("Version")]
    public string? Version { get; set; }

    private List<string>? tags = new();

    [JsonPropertyName("Tags")]
    public List<string>? Tags 
    { 
        get => tags ??= new List<string>(); 
        set => tags = value ?? new List<string>(); 
    }

    [JsonPropertyName("ModelDifference")]
    public ModelDifferenceData ModelDifference { get; set; } = new();

    [JsonPropertyName("Bones")]
    public Dictionary<string, BoneTransformData> Bones { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void OnDeserialized()
    {
        NormalizeBoneAliases();
    }

    public void NormalizeBoneAliases()
    {
        if (Bones == null || Bones.Count == 0) return;

        if (Bones.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            Bones = new Dictionary<string, BoneTransformData>(Bones, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var (alias, canonical) in BoneAliases)
        {
            if (Bones.TryGetValue(alias, out var boneData) && !Bones.ContainsKey(canonical))
            {
                Bones[canonical] = boneData;
            }
        }
    }

    public Vector3 GetRootOrPelvisPosition()
    {
        if (Bones == null || Bones.Count == 0) return Vector3.Zero;

        if (Bones.TryGetValue("j_kosi", out var pelvis) ||
            Bones.TryGetValue("j_sebo_a", out pelvis) ||
            Bones.TryGetValue("j_sebo_b", out pelvis) ||
            Bones.TryGetValue("n_hara", out pelvis) ||
            Bones.TryGetValue("n_root", out pelvis))
        {
            return pelvis.Position;
        }

        // Fallback: Centroid of all available bones for partial/prop poses
        Vector3 sum = Vector3.Zero;
        foreach (var b in Bones.Values)
        {
            sum += b.Position;
        }
        return sum / Bones.Count;
    }

    public static readonly Dictionary<string, string> BoneAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Root / Torso
        { "Root", "n_root" },
        { "Abdomen", "n_hara" },
        { "Throw", "n_throw" },
        { "Waist", "j_kosi" },
        { "Pelvis", "j_kosi" },
        { "Hips", "j_kosi" },
        { "Hip", "j_kosi" },
        { "Spine", "j_sebo_a" },
        { "Spine0", "j_sebo_a" },
        { "Spine1", "j_sebo_a" },
        { "SpineA", "j_sebo_a" },
        { "Spine2", "j_sebo_b" },
        { "SpineB", "j_sebo_b" },
        { "Spine3", "j_sebo_c" },
        { "SpineC", "j_sebo_c" },
        { "Chest", "j_sebo_c" },
        { "UpperChest", "j_sebo_c" },
        { "Neck", "j_kubi" },
        { "Head", "j_kao" },

        // Arms / Hands (Left)
        { "ClavicleLeft", "j_sako_l" },
        { "LeftClavicle", "j_sako_l" },
        { "ShoulderLeft", "j_kata_l" },
        { "LeftShoulder", "j_kata_l" },
        { "ArmLeft", "j_ude_a_l" },
        { "LeftArm", "j_ude_a_l" },
        { "UpperArmLeft", "j_ude_a_l" },
        { "LeftUpperArm", "j_ude_a_l" },
        { "ForearmLeft", "j_ude_b_l" },
        { "LeftForearm", "j_ude_b_l" },
        { "LowerArmLeft", "j_ude_b_l" },
        { "LeftLowerArm", "j_ude_b_l" },
        { "HandLeft", "j_te_l" },
        { "LeftHand", "j_te_l" },
        { "WristLeft", "j_te_l" },
        { "LeftWrist", "j_te_l" },

        // Arms / Hands (Right)
        { "ClavicleRight", "j_sako_r" },
        { "RightClavicle", "j_sako_r" },
        { "ShoulderRight", "j_kata_r" },
        { "RightShoulder", "j_kata_r" },
        { "ArmRight", "j_ude_a_r" },
        { "RightArm", "j_ude_a_r" },
        { "UpperArmRight", "j_ude_a_r" },
        { "RightUpperArm", "j_ude_a_r" },
        { "ForearmRight", "j_ude_b_r" },
        { "RightForearm", "j_ude_b_r" },
        { "LowerArmRight", "j_ude_b_r" },
        { "RightLowerArm", "j_ude_b_r" },
        { "HandRight", "j_te_r" },
        { "RightHand", "j_te_r" },
        { "WristRight", "j_te_r" },
        { "RightWrist", "j_te_r" },

        // Legs / Feet (Left)
        { "LegLeft", "j_asi_a_l" },
        { "LeftLeg", "j_asi_a_l" },
        { "LeftUpLeg", "j_asi_a_l" },
        { "UpLegLeft", "j_asi_a_l" },
        { "ThighLeft", "j_asi_a_l" },
        { "LeftThigh", "j_asi_a_l" },
        { "KneeLeft", "j_asi_b_l" },
        { "LeftKnee", "j_asi_b_l" },
        { "CalfLeft", "j_asi_c_l" },
        { "LeftCalf", "j_asi_c_l" },
        { "ShinLeft", "j_asi_c_l" },
        { "LeftShin", "j_asi_c_l" },
        { "FootLeft", "j_asi_d_l" },
        { "LeftFoot", "j_asi_d_l" },
        { "ToesLeft", "j_asi_e_l" },
        { "LeftToes", "j_asi_e_l" },
        { "ToeLeft", "j_asi_e_l" },
        { "LeftToe", "j_asi_e_l" },

        // Legs / Feet (Right)
        { "LegRight", "j_asi_a_r" },
        { "RightLeg", "j_asi_a_r" },
        { "RightUpLeg", "j_asi_a_r" },
        { "UpLegRight", "j_asi_a_r" },
        { "ThighRight", "j_asi_a_r" },
        { "RightThigh", "j_asi_a_r" },
        { "KneeRight", "j_asi_b_r" },
        { "RightKnee", "j_asi_b_r" },
        { "CalfRight", "j_asi_c_r" },
        { "RightCalf", "j_asi_c_r" },
        { "ShinRight", "j_asi_c_r" },
        { "RightShin", "j_asi_c_r" },
        { "FootRight", "j_asi_d_r" },
        { "RightFoot", "j_asi_d_r" },
        { "ToesRight", "j_asi_e_r" },
        { "RightToes", "j_asi_e_r" },
        { "ToeRight", "j_asi_e_r" },
        { "RightToe", "j_asi_e_r" },

        // Fingers (Left)
        { "ThumbALeft", "j_oya_a_l" },
        { "ThumbBLeft", "j_oya_b_l" },
        { "IndexALeft", "j_hito_a_l" },
        { "IndexBLeft", "j_hito_b_l" },
        { "MiddleALeft", "j_naka_a_l" },
        { "MiddleBLeft", "j_naka_b_l" },
        { "RingALeft", "j_kusu_a_l" },
        { "RingBLeft", "j_kusu_b_l" },
        { "PinkyALeft", "j_ko_a_l" },
        { "PinkyBLeft", "j_ko_b_l" },

        // Fingers (Right)
        { "ThumbARight", "j_oya_a_r" },
        { "ThumbBRight", "j_oya_b_r" },
        { "IndexARight", "j_hito_a_r" },
        { "IndexBRight", "j_hito_b_r" },
        { "MiddleARight", "j_naka_a_r" },
        { "MiddleBRight", "j_naka_b_r" },
        { "RingARight", "j_kusu_a_r" },
        { "RingBRight", "j_kusu_b_r" },
        { "PinkyARight", "j_ko_a_r" },
        { "PinkyBRight", "j_ko_b_r" },

        // Tail
        { "TailA", "j_sippo_a" },
        { "TailB", "j_sippo_b" },
        { "TailC", "j_sippo_c" },
        { "TailD", "j_sippo_d" },
        { "TailE", "j_sippo_e" },

        // Breasts
        { "BreastLeft", "j_mune_l" },
        { "BreastRight", "j_mune_r" },

        // Head / Face features
        { "Jaw", "j_ago" },
        { "EyeLeft", "j_me_l" },
        { "EyeRight", "j_me_r" },
        { "EarLeft", "j_mimi_l" },
        { "EarRight", "j_mimi_r" }
    };

    public class ModelDifferenceData
    {
        [JsonPropertyName("Position")]
        public string PositionString { get; set; } = "0, 0, 0";

        [JsonPropertyName("Rotation")]
        public string RotationString { get; set; } = "0, 0, 0, 1";

        [JsonPropertyName("Scale")]
        public string ScaleString { get; set; } = "1, 1, 1";

        [JsonIgnore]
        public Vector3 Position => ParseVector3(PositionString);

        [JsonIgnore]
        public Quaternion Rotation => ParseQuaternion(RotationString);
    }

    public class BoneTransformData
    {
        [JsonPropertyName("Position")]
        public string PositionString { get; set; } = "0, 0, 0";

        [JsonPropertyName("Rotation")]
        public string RotationString { get; set; } = "0, 0, 0, 1";

        [JsonPropertyName("Scale")]
        public string ScaleString { get; set; } = "1, 1, 1";

        [JsonIgnore]
        public Vector3 Position => ParseVector3(PositionString);

        [JsonIgnore]
        public Quaternion Rotation => ParseQuaternion(RotationString);
    }

    public static Vector3 ParseVector3(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return Vector3.Zero;
        var parts = str.Contains(',')
            ? str.Split(',')
            : str.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return Vector3.Zero;

        float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
        float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
        float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z);

        return new Vector3(x, y, z);
    }

    public static Quaternion ParseQuaternion(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return Quaternion.Identity;
        var parts = str.Contains(',')
            ? str.Split(',')
            : str.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return Quaternion.Identity;

        float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
        float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
        float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
        float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float w);

        return new Quaternion(x, y, z, w);
    }

    // Static mapping of actual limbs for a clean anatomy mannequin
    public static readonly (string Parent, string Child, float ThicknessMultiplier)[] MannequinBones = new[]
    {
        // Torso/Spine (Core Trunk)
        ("j_kosi", "j_sebo_a", 1.8f),
        ("j_sebo_a", "j_sebo_b", 1.8f),
        ("j_sebo_b", "j_sebo_c", 1.8f),
        ("j_sebo_c", "j_kubi", 1.0f),
        ("j_kubi", "j_kao", 1.4f), // Neck to Head

        // Left Arm
        ("j_sebo_c", "j_sako_l", 1.0f),
        ("j_sako_l", "j_ude_a_l", 1.3f),
        ("j_ude_a_l", "j_ude_b_l", 1.1f),
        ("j_ude_b_l", "j_te_l", 0.8f),

        // Right Arm
        ("j_sebo_c", "j_sako_r", 1.0f),
        ("j_sako_r", "j_ude_a_r", 1.3f),
        ("j_ude_a_r", "j_ude_b_r", 1.1f),
        ("j_ude_b_r", "j_te_r", 0.8f),

        // Left Leg
        ("j_kosi", "j_asi_a_l", 1.6f),
        ("j_asi_a_l", "j_asi_b_l", 1.4f),
        ("j_asi_b_l", "j_asi_c_l", 1.1f),
        ("j_asi_c_l", "j_asi_d_l", 0.9f),

        // Right Leg
        ("j_kosi", "j_asi_a_r", 1.6f),
        ("j_asi_a_r", "j_asi_b_r", 1.4f),
        ("j_asi_b_r", "j_asi_c_r", 1.1f),
        ("j_asi_c_r", "j_asi_d_r", 0.9f)
    };
}

public class PoseMetadataOnly
{
    [JsonPropertyName("Author")]
    public string? Author { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("Version")]
    public string? Version { get; set; }

    private List<string>? tags = new();

    [JsonPropertyName("Tags")]
    public List<string>? Tags 
    { 
        get => tags ??= new List<string>(); 
        set => tags = value ?? new List<string>(); 
    }
}
