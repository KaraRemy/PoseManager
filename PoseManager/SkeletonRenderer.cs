using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GposeManager;

public class SkeletonRenderer
{
    private interface IDrawCommand
    {
        float Depth { get; }
        void Draw(ImDrawListPtr drawList);
    }

    private class GridLineCommand : IDrawCommand
    {
        public float Depth { get; set; }
        public Vector2 Start { get; set; }
        public Vector2 End { get; set; }
        public uint Color { get; set; }

        public void Draw(ImDrawListPtr drawList)
        {
            drawList.AddLine(Start, End, Color, 1.0f);
        }
    }

    private class JointCommand : IDrawCommand
    {
        public float Depth { get; set; }
        public Vector2 Position { get; set; }
        public float Radius { get; set; }
        public uint Color { get; set; }
        public uint OutlineColor { get; set; }

        public void Draw(ImDrawListPtr drawList)
        {
            drawList.AddCircleFilled(Position, Radius, Color);
            drawList.AddCircle(Position, Radius, OutlineColor, 0, 1.0f);
        }
    }

    private class LimbCommand : IDrawCommand
    {
        public float Depth { get; set; }
        public Vector2 PA { get; set; }
        public Vector2 PB { get; set; }
        public float RA { get; set; }
        public float RB { get; set; }
        public uint Color { get; set; }
        public uint OutlineColor { get; set; }

        private static readonly Vector2[] pointsBuffer = new Vector2[14];

        public void Draw(ImDrawListPtr drawList)
        {
            int numSegments = 6;
            float angle = (float)Math.Atan2(PB.Y - PA.Y, PB.X - PA.X);
            int idx = 0;

            // Semi-circle around PB
            for (int i = 0; i <= numSegments; i++)
            {
                float theta = angle - (float)Math.PI / 2.0f + (float)Math.PI * i / numSegments;
                pointsBuffer[idx++] = PB + new Vector2((float)Math.Cos(theta) * RB, (float)Math.Sin(theta) * RB);
            }

            // Semi-circle around PA
            for (int i = 0; i <= numSegments; i++)
            {
                float theta = angle + (float)Math.PI / 2.0f + (float)Math.PI * i / numSegments;
                pointsBuffer[idx++] = PA + new Vector2((float)Math.Cos(theta) * RA, (float)Math.Sin(theta) * RA);
            }

            drawList.AddConvexPolyFilled(ref pointsBuffer[0], pointsBuffer.Length, Color);
            drawList.AddPolyline(ref pointsBuffer[0], pointsBuffer.Length, OutlineColor, ImDrawFlags.Closed, 1.0f);
        }
    }

    private static readonly List<IDrawCommand> commandsList = new(128);
    private static readonly Dictionary<string, (Vector2 pos, float depth, bool visible)> projectedBonesBuffer = new(64);
    private static readonly Comparison<IDrawCommand> depthComparer = (a, b) => b.Depth.CompareTo(a.Depth);

    public static void Render(ImDrawListPtr drawList, Camera camera, Configuration config, List<ViewportWindow.ActorInstance> actors)
    {
        commandsList.Clear();

        // 1. Generate Floor Grid Commands
        if (config.ShowGrid)
        {
            float gridY = 0.0f; // Ground level
            // Find lowest foot position if any actor is loaded to ground them
            foreach (var actor in actors)
            {
                if (actor.Pose == null) continue;
                foreach (var bone in actor.Pose.Bones.Values)
                {
                    // Look for foot/toe bones to determine ground offset
                    var worldPos = actor.GetOffsetPosition(bone.Position);
                    if (worldPos.Y < gridY)
                    {
                        gridY = worldPos.Y;
                    }
                }
            }

            // Draw grid centered around target XZ plane
            float range = 1.5f;
            float step = 0.25f;
            float centerX = (float)Math.Round(camera.Target.X / step) * step;
            float centerZ = (float)Math.Round(camera.Target.Z / step) * step;

            for (float x = centerX - range; x <= centerX + range; x += step)
            {
                var startVal = new Vector3(x, gridY, centerZ - range);
                var endVal = new Vector3(x, gridY, centerZ + range);

                var (pStart, pEnd, avgDepth, lineVisible) = ProjectLine(camera, startVal, endVal);

                if (lineVisible)
                {
                    float fade = GetDepthFade(avgDepth, camera.Zoom, config.EnableDepthShading);
                    uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f * fade, 0.5f * fade, 0.5f * fade, 0.35f * fade));

                    commandsList.Add(new GridLineCommand
                    {
                        Depth = avgDepth,
                        Start = pStart,
                        End = pEnd,
                        Color = color
                    });
                }
            }

            for (float z = centerZ - range; z <= centerZ + range; z += step)
            {
                var startVal = new Vector3(centerX - range, gridY, z);
                var endVal = new Vector3(centerX + range, gridY, z);

                var (pStart, pEnd, avgDepth, lineVisible) = ProjectLine(camera, startVal, endVal);

                if (lineVisible)
                {
                    float fade = GetDepthFade(avgDepth, camera.Zoom, config.EnableDepthShading);
                    uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f * fade, 0.5f * fade, 0.5f * fade, 0.35f * fade));

                    commandsList.Add(new GridLineCommand
                    {
                        Depth = avgDepth,
                        Start = pStart,
                        End = pEnd,
                        Color = color
                    });
                }
            }
        }

        // 2. Generate Mannequins Drawing Commands
        for (int a = 0; a < actors.Count; a++)
        {
            var actor = actors[a];
            if (actor.Pose == null) continue;

            var baseColor = actor.Color;

            // Cache projected screen coordinates and depths of all loaded bones into reusable buffer
            projectedBonesBuffer.Clear();
            foreach (var kp in actor.Pose.Bones)
            {
                var worldPos = actor.GetOffsetPosition(kp.Value.Position);
                projectedBonesBuffer[kp.Key] = camera.Project(worldPos);
            }

            // Generate Limb (Stretched Ellipse) Commands
            foreach (var connection in PoseData.MannequinBones)
            {
                if (projectedBonesBuffer.TryGetValue(connection.Parent, out var parentProj) &&
                    projectedBonesBuffer.TryGetValue(connection.Child, out var childProj))
                {
                    if (parentProj.visible && childProj.visible)
                    {
                        float avgDepth = (parentProj.depth + childProj.depth) * 0.5f;
                        float fade = GetDepthFade(avgDepth, camera.Zoom, config.EnableDepthShading);

                        // Perspective thickness scaling (closer is thicker)
                        float baseThick = 6.0f * config.LimbThickness * connection.ThicknessMultiplier;
                        float rParent = Math.Clamp(baseThick / parentProj.depth, 1.0f, 40.0f);
                        float rChild = Math.Clamp(baseThick / childProj.depth, 1.0f, 40.0f);

                        uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(baseColor.X * fade, baseColor.Y * fade, baseColor.Z * fade, 0.85f * fade));
                        uint outlineColor = ImGui.ColorConvertFloat4ToU32(new Vector4(baseColor.X * 0.4f * fade, baseColor.Y * 0.4f * fade, baseColor.Z * 0.4f * fade, 0.95f * fade));

                        commandsList.Add(new LimbCommand
                        {
                            Depth = avgDepth,
                            PA = parentProj.pos,
                            PB = childProj.pos,
                            RA = rParent,
                            RB = rChild,
                            Color = color,
                            OutlineColor = outlineColor
                        });
                    }
                }
            }

            // Generate Joint Sphere Commands
            foreach (var kp in projectedBonesBuffer)
            {
                // We only render joint spheres for standard mannequin joints
                bool isMannequinJoint = false;
                foreach (var conn in PoseData.MannequinBones)
                {
                    if (conn.Parent == kp.Key || conn.Child == kp.Key)
                    {
                        isMannequinJoint = true;
                        break;
                    }
                }

                if (isMannequinJoint && kp.Value.visible)
                {
                    float fade = GetDepthFade(kp.Value.depth, camera.Zoom, config.EnableDepthShading);
                    
                    // Extra size for the head
                    float sizeMultiplier = (kp.Key == "j_kao") ? 6.6f : 1.0f;
                    float baseRadius = 8.0f * config.JointSize * sizeMultiplier;
                    float radius = Math.Clamp(baseRadius / kp.Value.depth, 1.0f, 50.0f);

                    uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(baseColor.X * 1.1f * fade, baseColor.Y * 1.1f * fade, baseColor.Z * 1.1f * fade, 0.95f * fade));
                    uint outlineColor = ImGui.ColorConvertFloat4ToU32(new Vector4(baseColor.X * 0.3f * fade, baseColor.Y * 0.3f * fade, baseColor.Z * 0.3f * fade, 0.95f * fade));

                    commandsList.Add(new JointCommand
                    {
                        Depth = kp.Value.depth,
                        Position = kp.Value.pos,
                        Radius = radius,
                        Color = color,
                        OutlineColor = outlineColor
                    });
                }
            }
        }

        // 3. Sort Commands Back-to-Front (Painters Algorithm)
        commandsList.Sort(depthComparer);

        // 4. Dispatch Draw Calls (Clipped strictly inside Viewport bounds)
        drawList.PushClipRect(camera.CanvasStart, camera.CanvasStart + camera.CanvasSize, true);
        foreach (var cmd in commandsList)
        {
            cmd.Draw(drawList);
        }
        drawList.PopClipRect();
    }

    private static float GetDepthFade(float depth, float zoom, bool enabled)
    {
        if (!enabled) return 1.0f;
        // Fog/fade maps from Zoom - 1.0 (fully lit) to Zoom + 1.2 (faded out)
        float start = zoom - 0.8f;
        float end = zoom + 1.5f;
        float t = (depth - start) / (end - start);
        return 1.0f - Math.Clamp(t, 0.0f, 0.65f); // Floor opacity at 0.35f
    }

    private static (Vector2 Start, Vector2 End, float AvgDepth, bool Visible) ProjectLine(Camera camera, Vector3 a, Vector3 b)
    {
        var (pA, depthA, visA) = camera.Project(a);
        var (pB, depthB, visB) = camera.Project(b);

        if (visA && visB)
        {
            return (pA, pB, (depthA + depthB) * 0.5f, true);
        }

        if (!visA && !visB)
        {
            return (Vector2.Zero, Vector2.Zero, 0f, false);
        }

        // One is visible, one is not. Clip against near plane.
        float near = 0.05f; // near plane depth threshold
        float t = (near - depthA) / (depthB - depthA);
        t = Math.Clamp(t, 0.0f, 1.0f);

        Vector3 clippedPoint = a + t * (b - a);
        var (pC, depthC, visC) = camera.Project(clippedPoint);

        float avgDepth = (Math.Max(near, depthA) + Math.Max(near, depthB)) * 0.5f;

        if (!visA)
        {
            // A was invisible, so replace A with C
            return (pC, pB, avgDepth, visC);
        }
        else
        {
            // B was invisible, so replace B with C
            return (pA, pC, avgDepth, visC);
        }
    }
}
