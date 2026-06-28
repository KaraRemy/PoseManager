using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GposeManager;

public class PoseData
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
    public Dictionary<string, BoneTransformData> Bones { get; set; } = new();

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
        var parts = str.Split(',');
        if (parts.Length < 3) return Vector3.Zero;

        float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
        float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
        float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z);

        return new Vector3(x, y, z);
    }

    public static Quaternion ParseQuaternion(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return Quaternion.Identity;
        var parts = str.Split(',');
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
