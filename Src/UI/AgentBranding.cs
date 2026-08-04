using System.Drawing;

namespace AgenteBiometricoPresencial.UI;

internal static class AgentBranding
{
    public static Icon LoadApplicationIcon() =>
        Icon.ExtractAssociatedIcon(Application.ExecutablePath) ??
        (Icon)SystemIcons.Shield.Clone();
}
