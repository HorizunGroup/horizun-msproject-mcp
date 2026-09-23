namespace Horizun.ProjectMcp.Backends;

/// <summary>
/// The things only an installed Microsoft Project can do, on top of <see cref="ProjectAutomation"/>.
/// </summary>
public static class ComBridge
{
    /// <summary>
    /// Writes a genuine binary .mpp by having Microsoft Project open the schedule and save it in its
    /// own format. This is the only way the format can be authored at all.
    /// </summary>
    /// <remarks>
    /// The schedule goes to Project through <see cref="ProjectHandoff"/>, not a plain MSPDI export:
    /// a plain export saved this way came back with different durations on a real schedule, because
    /// Project re-derives them from placeholder assignments and loses split tasks' gaps.
    /// </remarks>
    public static void SaveAsMpp(MPXJ.Net.ProjectFile project, string targetMppPath)
    {
        var token = $"hzpm-{Guid.NewGuid():N}";
        var staging = Path.Combine(Path.GetTempPath(), token + ".xml");
        try
        {
            ProjectHandoff.Write(project, staging);
            ProjectAutomation.Run(host =>
            {
                host.Open(staging, token);
                host.SaveMpp(token, targetMppPath);
                host.Close(token);
                return 0;
            });
        }
        catch (McpToolException ex)
        {
            throw new McpToolException(
                $"Microsoft Project could not write the .mpp: {ex.Message} Write MSPDI instead "
                + "(format='mspdi' — a .xml Microsoft Project opens natively), or run project_health with "
                + "deep=true to check the COM server.");
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch
            {
                // A leftover temp file is not worth failing the save over.
            }
        }
    }
}
