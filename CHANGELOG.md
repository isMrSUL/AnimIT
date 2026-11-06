# Changelog

## 2.1.0 — first public release

**Editor**
- New AnimIT logo and app icon.
- Moves are shown by their real parts (*Jump*, *Landing on a lying opponent*, *Approach*, *Hit*, *Put on the hold*,
  *Hold (loops)*, *Let go of the hold*…) instead of phase numbers; empty 1-frame parts are hidden.
- **Add whole move**: adds every part the game plays for a move, in order.
- **Smooth** joins between clips (the wrestlers keep moving through the cut).
- **Split view**: preview a library move next to your timeline.
- **Events strip**: see and move the hits, ring shakes, rumbles and sounds of a spliced move; Save to game uses
  them.
- **Save to game**: choose the move type and the donor; the window tells you which Create-a-Moveset list the move
  goes into.
- A move's own approach + hit are lined up by the receiver, like in the game.

**Saving**
- Moves saved on a hidden donor also get a Create-a-Moveset preview copy (their preview used to stay still).
- Frame counts of copied donor parts are taken from the animation data (some game tables list 0 frames).

**Porter (HCTP / WWE 2K26 / AEW Fight Forever)**
- Donor finder ("Best matches"), move types for AEW, 3D source preview, event editor with AEW markers.
- AEW dives: AEW's jump + AEW's landings on an SvR 2011 dive donor.
- The porter now always uses the SvR 2011 game chosen in AnimIT (it could read a fixed folder before).
- 2K26 archives use the Oodle DLL from your own WWE 2K26 install (it is not shipped with AnimIT).
- The release includes its own small Python, so the porter works without installing anything.
