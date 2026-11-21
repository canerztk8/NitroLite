# Hardware compatibility pilot

Collect one report per **model, board, BIOS, OS and driver/kernel**. Settings → Diagnostics exports the hardware report. On Linux also run `nitrolite --linux-selftest` from the installed package and save the capture from Settings → Linux setup. Use the physical observation column of the report to record what happened. Review locally before submitting a compatibility issue.

| Pilot machine | First measurement | What settles the question |
| --- | --- | --- |
| AN16-41 | Compare the two fan RPM readings with the Acer utility. Record Windows and native Ubuntu separately. | Both fans track their respective readings, followed by a successful return to automatic mode. |
| AN16-61 | Capture a single change in charge limit. Test MUX separately on a machine available for a reboot. | Charge limit reads back; active GPU mode after the reboot agrees with the requested mode. |
| PHN16S-71 or ANV16S-41 | Record the keyboard controller identity, then change one zone with the installed Acer utility. | The named zone visibly changes and can be restored; distinguish an accepted command from a visible change. |
| NL16-71G | Capture model, BIOS, controller IDs, available sensors and the brightness keys' effect. | Establish which controls and interfaces exist on this SKU before implementing writes. Single-colour brightness and RGB are separate capabilities. |

Capture A, change one setting using a supported control, capture B, restore the original setting, and capture again. Repeat to separate unrelated temperature fluctuations from reproducible changes. For a fan test observe the tachometer at a steady load, return both fans to automatic and record the result. Skip controls that are absent. Record lack of permission separately from an absent interface. Linux self-test checks access and does not certify fan movement or a visible colour change.

For a new controller, retain the model-level capture as a test fixture, document the exact interface and supported operations, and add a backend only for the identified controller. Re-run the capture test and the physical observation before promoting the feature to verified for that configuration.
