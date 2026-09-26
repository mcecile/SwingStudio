namespace SwingStudio.Capture;

/// <summary>The ProTee JPEG burst is prefixed with this text. Ball-presence status messages are not.</summary>
public static class ShotTrigger
{
    private static ReadOnlySpan<byte> Marker => "shotTrigger"u8;

    public static bool Contains(ReadOnlySpan<byte> payload) => payload.IndexOf(Marker) >= 0;
}
