namespace NitroLite.Core.Hardware.Lighting;

/// <summary>
/// A vendor HID controller that can carry a four-zone keyboard colour, as seen by the platform.
/// </summary>
/// <remarks>
/// <para>
/// The interface exists so the transport decision in <see cref="KeyboardLightingRouter"/> can be unit-tested without a
/// keyboard attached: a test supplies a writer whose <see cref="TryWrite"/> answers whatever the case needs, and the
/// router's WMI-to-HID fallback is exercised with no Windows API in sight.
/// </para>
/// <para>
/// <see cref="CanWrite"/> is separate from existing at all because a controller can be recognised and still refuse: a
/// device whose lighting frame nobody documented must be listed in the report — it tells the next person which
/// controller the machine has — while never receiving a guessed packet.
/// </para>
/// </remarks>
public interface IHidLightingWriter : IDisposable
{
    /// <summary>The controller, e.g. <c>ENE KB5130 0x0CF2:0x5130</c>, for the log and the report.</summary>
    string Description { get; }

    /// <summary>The protocol name, e.g. <c>ene-kb5130</c>.</summary>
    string Protocol { get; }

    /// <summary>True when a documented frame exists for this controller, so a write may be attempted.</summary>
    bool CanWrite { get; }

    /// <summary>Why <see cref="CanWrite"/> is false. Null when it is true.</summary>
    string? Refusal { get; }

    /// <summary>
    /// Sends one frame. Returns false with a reason rather than throwing: a device that is unplugged mid-write is a
    /// keyboard that keeps its last colour, not an application that crashes.
    /// </summary>
    bool TryWrite(KeyboardLightingFrame frame, out string? error);
}
