# Six Stars integration

Based on the supplied Resonator-dotnet-v0.1.zip. Core/API/CLI retained.

Changes: preserve numeric slashes in the tokenizer (meters and fractional onset
offsets); resolve relative API instrument profile paths against the output folder.

The desktop posts JSON with the notation property to /midi and receives binary MIDI.
Use ../../start-resonator.cmd from the Six Stars solution root.
