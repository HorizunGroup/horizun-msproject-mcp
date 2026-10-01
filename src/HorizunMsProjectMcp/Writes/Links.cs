using MPXJ.Net;

namespace Horizun.ProjectMcp.Writes;

/// <summary>Removing a dependency so that both ends forget it.</summary>
/// <remarks>
/// MPXJ keeps a task's predecessors and its predecessor's successors in two lists. Removing a link
/// from the first left it in the second, and everything that walks forward — cycle detection, the
/// internal critical-path engine, open-end checks — went on seeing a link the file no longer had:
/// <c>links_write</c> refused a link for a "cycle" through one removed earlier, in the same batch or
/// on the same open document, until the file was reopened.
/// </remarks>
public static class Links
{
    public static void Remove(ProjectFile project, Relation relation)
    {
        // The project's relation container keeps both lists; removing through it is the one call
        // that updates them together.
        project.Relations.Remove(relation);

        // Belt and braces: whichever list still holds it after that, drop it from there too.
        relation.SuccessorTask?.Predecessors.Remove(relation);
        relation.PredecessorTask?.Successors.Remove(relation);
    }
}
