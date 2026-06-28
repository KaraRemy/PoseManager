using System;
using System.Numerics;

namespace GposeManager;

public class Camera
{
    public float Yaw { get; set; } = 0.0f;     // Horizontal rotation (radians)
    public float Pitch { get; set; } = 0.15f;  // Vertical rotation (radians)
    public float Zoom { get; set; } = 2.5f;     // Distance from target (meters)
    public Vector3 Target { get; set; } = new Vector3(0, 0.7f, 0); // Look-at target (centered around character chest/spine)

    public Vector2 CanvasSize { get; set; } = new Vector2(400, 400);
    public Vector2 CanvasStart { get; set; } = new Vector2(0, 0);
    public float Fov { get; set; } = 45f;       // Field of view (degrees)

    public (Vector2 ScreenPos, float Depth, bool Visible) Project(Vector3 worldPos)
    {
        // 1. Calculate camera position on a sphere around the Target
        // FFXIV standard coordinate system: Y is vertical Up.
        float cosPitch = (float)Math.Cos(Pitch);
        float sinPitch = (float)Math.Sin(Pitch);
        float cosYaw = (float)Math.Cos(Yaw);
        float sinYaw = (float)Math.Sin(Yaw);

        var cameraOffset = new Vector3(
            Zoom * cosPitch * sinYaw,
            Zoom * sinPitch,
            Zoom * cosPitch * cosYaw
        );
        var cameraPos = Target + cameraOffset;

        // 2. View Matrix
        var up = Vector3.UnitY;
        var viewMatrix = Matrix4x4.CreateLookAt(cameraPos, Target, up);

        // 3. Projection Matrix
        float aspect = CanvasSize.X / Math.Max(1.0f, CanvasSize.Y);
        var projMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
            (float)(Fov * Math.PI / 180.0),
            aspect,
            0.1f,
            100.0f
        );

        // 4. View-Projection Matrix
        var viewProj = viewMatrix * projMatrix;

        // 5. Transform worldPos to clip space
        var clipPos = Vector4.Transform(new Vector4(worldPos, 1.0f), viewProj);

        // 6. Perspective Division (behind camera check)
        if (clipPos.W <= 0.001f)
        {
            return (Vector2.Zero, clipPos.W, false);
        }

        float ndcX = clipPos.X / clipPos.W;
        float ndcY = clipPos.Y / clipPos.W;

        // 7. Map NDC [-1, 1] to Canvas Screen Coordinates
        float screenX = CanvasStart.X + (ndcX + 1.0f) * 0.5f * CanvasSize.X;
        float screenY = CanvasStart.Y + (1.0f - ndcY) * 0.5f * CanvasSize.Y; // Invert Y for screen coordinates

        return (new Vector2(screenX, screenY), clipPos.W, true);
    }

    public void Reset()
    {
        Yaw = 0.0f;
        Pitch = 0.15f;
        Zoom = 2.5f;
        Target = new Vector3(0, 0.7f, 0);
    }
}
