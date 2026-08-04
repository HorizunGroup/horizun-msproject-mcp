using System.ComponentModel;
using Horizun.ProjectMcp.Diagnostics;
using ModelContextProtocol.Server;

namespace Horizun.ProjectMcp.Tools;

[McpServerToolType]
public static class HealthTool
{
    [McpServerTool(Name = "project_health")]
    [Description(
        "Diagnose what this server can do on this machine. Call it first in every session. "
        + "Reports the active backend, runtime, whether Microsoft Project is installed AND whether its "
        + "COM server actually starts, and a capability matrix the other tools honour. "
        + "When something is wrong it returns the exact diagnosis and a repair path, not a stack trace. "
        + "Pass deep=true to genuinely launch Microsoft Project instead of only reading its registration — "
        + "slower and visible on screen, but the only way to know COM works.")]
    public static HealthReport ProjectHealth(
        [Description(
            "Launch the Microsoft Project COM server for real instead of only inspecting its registration. "
            + "Starts Project and closes it again. Defaults to false.")]
        bool deep = false)
        => EnvironmentDoctor.Run(deep);
}
