namespace Gtamp.Server.Admin
{
    /// <summary>
    /// Something that can run an administrative command line and return its output.
    /// <para>
    /// It exists so <see cref="AdminConsole"/> — which lives above the server and
    /// owns stdin — can be handed to the server for network admin commands without
    /// the server depending on the console's construction. One command table, two
    /// front ends, no drift.
    /// </para>
    /// </summary>
    public interface IAdminSurface
    {
        string Execute(string commandLine);
    }

    /// <summary>
    /// Used when no console has been attached. Says so rather than returning an
    /// empty string, because "nothing happened and nothing was said" is the hardest
    /// possible thing to diagnose from the other end of a network.
    /// </summary>
    public sealed class UnavailableAdminSurface : IAdminSurface
    {
        public string Execute(string commandLine) =>
            "This server is not accepting administrative commands over the network.";
    }
}
