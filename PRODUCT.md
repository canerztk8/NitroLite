# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

## Users

One user: the owner of this Acer Nitro laptop. They want fan, power mode, keyboard backlight, battery and MUX behaviour managed from one place while gaming and during everyday use. Not having to open NitroSense is the main motivation. It began as a personal tool; it is published now, but there is still no onboarding, no multi-account story and no distribution scenario beyond "download the zip and run it".

## Product Purpose

NitroLite is a lightweight Windows desktop application that replaces NitroSense on Acer Nitro laptops. It talks directly to AcerAgentService (127.0.0.1:46933) and Acer Care Center (wss://localhost:4343) to manage the operating mode, fan speed, keyboard backlight, battery charge limit, MUX switch and hardware telemetry. Success: the user can control all of the hardware and understand what it is doing without ever opening NitroSense.

## Positioning

The same hardware control, delivered in a single process with no extra background service and no telemetry layer in between. The protocol speaks to Acer's own agent; nothing runs while the application is closed.

## Operating Context

- Windows 11, .NET 10 WPF, single-file self-contained release.
- The application lives in the tray; telemetry and game detection keep running when the window is closed.
- A game profile is applied automatically when a game starts and the desktop profile is restored when it ends.
- ATK wireless mouse profiles (Config 1-4) are supported; the mouse may not answer while asleep, and the command is retried.
- Some paths need administrator rights (standby purge, disk temperature); without them the value is not invented.

## Capabilities and Constraints

- Operating modes: Quiet, Balanced, Performance, Turbo, Eco.
- Fan: Auto, Max, Custom (CPU/GPU 30-100 %).
- Keyboard backlight: effect, brightness, speed, direction, colour, per-zone LEDs.
- Battery: charge limit, health, thermal health, cycle count.
- Hardware monitoring and an OSD overlay.
- MUX switch (requires a restart).
- System services, debloat and repair tools.
- Constraint: lightweight. Idle CPU and RAM consumption must stay low; no continuous animation, heavy effects or large visual layers.
- Constraint: no invented data. An unreadable value is shown as `--`, never guessed.
- Languages: English, Turkish, German, Spanish, Russian, Portuguese, Hindi and Indonesian.

## Brand Commitments

- Name: NitroLite, version 1.3.0, MIT licence.
- No emoji.
- The visual identity is not binding: the user asked for "more modern", so the original dark theme and accent colours did not have to survive. What did survive is the rule that state is never carried by colour alone.

## Evidence on Hand

- A working application and a published package (`artifacts/`).
- 1623 tests: 1620 pass, 3 live-hardware tests skipped.
- Real hardware telemetry verified on this machine (CPU/GPU temperature, fan RPM, battery, ATK receiver).
- No customers, references, benchmarks or press coverage to invent.

## Product Principles

1. Lightweight is a feature: a new visual layer only goes in if it does not raise idle consumption.
2. Honest telemetry: an unreadable value is not invented, it is shown as `--`.
3. One window, one process: the user never has to open NitroSense.
4. Feedback at the moment of action: every hardware command reports its outcome (accepted, refused, no answer, retrying).
5. Eight interface languages, familiar Windows behaviour.

## Accessibility & Inclusion

- Keyboard navigation (the existing RadioButton-based navigation is kept).
- Readability at high DPI (a 2560x1380 scaled screen).
- State is stated in text as well as colour.
