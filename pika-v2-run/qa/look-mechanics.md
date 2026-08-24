# Pika look mechanics

Pika is a compact pixel-art electric mouse. Keep the feet and lower torso anchored to one shared baseline. The head and face lead the gaze in small stepped pixel changes; ears follow the head, and the lightning-bolt tail stays attached to the rear and lags one small step through turns. Preserve the original black pixel eyes and red cheeks; do not add replacement eyes or slide detached pupils.

Cardinal pose families in viewer coordinates:

- `000` up: frontal body, ears/head tip and eyes aim toward the top; tail stays rear-anchored.
- `090` screen-right: right-facing head/eye emphasis, more left cheek/body occluded; tail follows slightly behind.
- `180` down: frontal body, eyelids/eyes and head angle aim toward the bottom; feet remain fixed.
- `270` screen-left: inverse of right, with screen-left head/eye emphasis and the tail trailing on the rear side.

Intermediates use even 22.5-degree pixel steps between these families. Do not rotate or skew the whole sprite. Keep scale, baseline, silhouette, and tail attachment continuous across every adjacent direction.
