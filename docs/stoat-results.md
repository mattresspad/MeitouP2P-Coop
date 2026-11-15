# Stoat Scout playtest

## Stock Windows 0.20.1

On November 15, 2026, two stock Windows clients from `8c79093d` ran on one PC over IPv4 loopback. Both loaded the packages built for `=0.20.1` and the Stoat fixture in fresh profiles with autosave off. The guest used body clicks, terrain right-clicks and A/D camera input. The replay pairs captures from the two windows. [0.20.1 replay](stoat-walk-0.20.1.gif) · [Stock release](https://github.com/VlcekM/MeitouClient/releases/tag/v0.20.1)

| Action | Observed in the two clients |
| --- | --- |
| The guest selected the Scout's body and right-clicked the ground. The host selected the Guide and sent him in another direction. | The first Scout move reached the host at tick 289. Both windows showed both characters moving. The guest retained the Scout and the host retained the Guide. |
| The guest panned right, cleared selection and waited for arrival. | The Scout went off-screen. The guest camera pivot moved from X 34665.0 to 35135.2. The host pivot stayed at X 34665.0, Z −70142.0 throughout the walk. |
| The guest panned back and clicked the former body position, then the body at arrival. | Pixel (151, 260) hit terrain and selected nobody. Pixel (724, 265) hit the Scout's original guest-local character `c:0.0` at X 34778.9, Z −70158.0. The guest HUD showed Stoat Scout and the host still had Stoat Guide selected. |
| The guest right-clicked again, then attempted a Guide-only and a mixed-selection order. | The second Scout move reached the host at tick 993. The guest saw the Scout reach X 34692.2, Z −70133.7. The two refused clicks produced zero further host orders. The captured run logged exactly two guest moves. |

The same private input and snapshot hooks worked on both releases. Build the mods for the release you play. In the separate loader tests, mixed 0.20.0 and 0.20.1 peers were refused in both directions. Matching peers passed ping, movement, stale-grant, reconnect and host-world replacement checks. Both 0.20.1 peers also applied the tagged Maestro export. [Maestro role test](maestro.md) · [Reproduce the walk](https://github.com/mattresspad/MeitouP2P#walk-stoat-scout-in-two-windows)

## Stock Windows 0.20.0

Recorded on November 15, 2026 with stock Windows MeitouClient 0.20.0 (`da6af12e`), two disposable profiles, autosave disabled, and one IPv4 loopback connection. The two windows ran the connector, Co-op and the matching Stoat fixture. The replay combines actual screenshots at selected stages. [Side-by-side replay](stoat-walk.gif)

| Input | Observed outcome |
| --- | --- |
| The guest selected the Scout by her body and right-clicked a destination. The host selected the Guide and right-clicked elsewhere. | Both characters moved in both windows. The host accepted the Scout order at tick 274. |
| The guest panned with the camera keys, cleared selection and waited. | The Scout moved off the guest's screen. The host kept the Guide selected and its camera pivot stayed fixed. |
| The guest returned, clicked the Scout's old position and then clicked her body at arrival. | The old position missed. At the arrival check the guest selected the Scout at X 34778.9, Z −70158.0, while the host retained the Guide. |
| The guest right-clicked a second destination. | The host accepted the second Scout order at tick 1021. Two accepted ground clicks produced exactly two host move orders. |
| The guest selected the Guide alone and then both characters and tried another ground move with each selection. | The HUD explained that only the Scout takes guest orders. The host received zero additional guest move orders. |

In the separate loader test on synthetic flat ground, the guest observed the assigned actor at X 30.00 on tick 3 and X 399.42 on tick 453. An incompatible mode set was refused. Reassignment invalidated the previous grant. Reconnecting and replacing the host world both required a fresh offer. The standalone connector also completed a handshake, ping and leave without Co-op.

Both visible trials draw host poses onto matching characters in the guest fixture. Their scope is movement, picking and independent cameras on one PC. Two-machine play, campaign save continuity and whole-world replication need their own checks. [Replay notice](../LICENSING.md#stoat-replay)
