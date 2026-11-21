# Windows NitroSense package comparison

On Windows, the dashboard automatically reads the legacy service on Acer machines when the primary telemetry source returns no sample. This works without a model-specific catalog entry. Missing readings remain unknown; a legacy temperature alone does not establish a verified fan-curve sensor mapping. An absent service is retried after 30 seconds.

Run `NitroLite.Desktop.exe --pssvc-sensors` from a terminal on Windows. Exit code 0 means all four sensor replies contained plausible values. Exit code 1 means at least one reading was unavailable or the service connection failed.

Inspected 2026-11-21 without installing either package. Package declarations describe the OEM software and are not measurements of a particular keyboard or fan.

| Acer support listing | Package evidence | NitroLite path |
| --- | --- | --- |
| [AN515-57](https://www.acer.com/us-en/support/product-support/AN515-57/NH.QF1AL.002), [AN515-58](https://www.acer.com/us-en/support/product-support/AN515-58/NH.QMZAL.00A/downloads) | 3.01.3056. Both downloads had SHA-256 F9185452FD6BAF23AC33F417A35F98686E202E10F1BD1745908E5BFCAB971622. The ServiceInstall table of NitroSense.msi names PSSvc. Each model has its own Plugs folder. Both HW_Support.ini files declare CPU and GPU fans, no system fan, and contain zone colour adjustments and a keyboard backlight setting. | Existing direct WMI depends on firmware availability. New explicit Windows diagnostic: --pssvc-sensors reads CPU/GPU temperatures and RPM from the legacy service. The fan slider and lighting controls are not wired to this diagnostic. |
| [AN16-41](https://www.acer.com/us-en/support/product-support/AN16-41/) | 5.2.84. ZIP directory inspected by a range read. The x64 AgentService files include AcerAgentService.exe, AcerHardwareService.exe and AcerLightingService.exe. On the reference Windows installation AASSvc was running. | Existing AcerAgentClient uses the local Acer agent. Service presence alone does not verify that a fan or light changed. |

The [RESense protocol reference](https://github.com/mdonmez/RESense/blob/main/docs/re/protocol.md) documents separate operations for fan telemetry, mode and manual speed, and separate brightness, static zone colour and zone enablement operations. Its hardware target is AN515-58. AN515-57 shares the installer; an actual reading on that model is still needed. The diagnostic only uses the service telemetry operation, with a bounded wait and plausibility checks. Its offline test uses a synthetic getter reply; no OEM response capture was available. Validate the four readings against NitroSense on the same machine before using this transport in automatic control.
