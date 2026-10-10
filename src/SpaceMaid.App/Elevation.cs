using System.Security.Principal;

namespace SpaceMaid.App;

internal static class Elevation
{
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);
}
