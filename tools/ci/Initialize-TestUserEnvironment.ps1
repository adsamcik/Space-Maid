function Initialize-TestUserEnvironment {
    if (-not $IsWindows) { throw 'A Windows user environment is required.' }
    if (-not ('SpaceMaid.CI.UserEnvironment' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SpaceMaid.CI {
    public static class UserEnvironment {
        public static void RestoreProfileVariables() {
            using var identity = WindowsIdentity.GetCurrent();
            if (!CreateEnvironmentBlock(out var block, identity.AccessToken.DangerousGetHandle(), false))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var profileNames = new HashSet<string>(new[] {
                "USERPROFILE", "LOCALAPPDATA", "APPDATA", "HOMEDRIVE", "HOMEPATH",
                "USERNAME", "USERDOMAIN", "TEMP", "TMP"
            }, StringComparer.OrdinalIgnoreCase);
            var profileVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try {
                var pointer = block;
                while (Marshal.ReadInt16(pointer) != 0) {
                    string entry = Marshal.PtrToStringUni(pointer);
                    pointer = IntPtr.Add(pointer, (entry.Length + 1) * 2);
                    int separator = entry.IndexOf('=');
                    if (separator <= 0) continue;
                    string name = entry.Substring(0, separator);
                    // Preserve setup-dotnet PATH and CI markers. Only these
                    // identity/profile variables come from the loaded user.
                    if (profileNames.Contains(name)) profileVariables[name] = entry.Substring(separator + 1);
                }
            } finally {
                if (!DestroyEnvironmentBlock(block)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            foreach (string name in new[] { "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
                if (!profileVariables.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
                    throw new InvalidOperationException("The test account's loaded profile could not be resolved.");
            foreach (string name in profileNames)
                // Freshly created accounts can omit optional home variables.
                // Remove inherited values instead of retaining runneradmin's.
                Environment.SetEnvironmentVariable(name, profileVariables.TryGetValue(name, out var value) ? value : null);
        }
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token,
            [MarshalAs(UnmanagedType.Bool)] bool inherit);
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    }
}
'@
    }
    [SpaceMaid.CI.UserEnvironment]::RestoreProfileVariables()
}
