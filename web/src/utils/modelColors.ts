import type { CSSProperties } from 'react';
import type { ModelSettingsRow } from '../types';

// Models of one provider share an icon colour, so a per-model colour needs its
// own derivation: a stable hash of the model name picks a hue, and the golden
// angle spreads the hues of a handful of models instead of letting them land
// next to each other.
const GOLDEN_ANGLE = 137.508;
const SATURATION = 58;
const LIGHTNESS = 47;

function hash(value: string): number {
  let result = 2166136261;
  for (let index = 0; index < value.length; index++) {
    result ^= value.charCodeAt(index);
    result = Math.imul(result, 16777619);
  }
  return result >>> 0;
}

// A full hue circle keeps the models of one provider apart, but two bands read
// badly next to the rest of the dashboard: pure red (the error colour) and
// yellow-lime (muddy). Red becomes rose/magenta and lime becomes teal, so every
// derived colour stays usable next to the metrics it decorates.
function usableHue(hue: number): number {
  if (hue >= 340) {
    return 300 + (hue - 340);
  }
  if (hue < 20) {
    return 320 + hue;
  }
  if (hue >= 45 && hue < 105) {
    return 160 + ((hue - 45) / 60) * 60;
  }
  return hue;
}

/**
 * Colour identifying a model, or null for the whole-session view. A colour set
 * in the model settings wins so a hand-picked one stays stable everywhere.
 */
export function modelAccent(
  model: string | undefined,
  profile?: ModelSettingsRow | null,
): string | null {
  if (!model) {
    return null;
  }

  if (profile?.iconColor) {
    return profile.iconColor;
  }

  const hue =
    Math.round(usableHue((hash(model.toLowerCase()) * GOLDEN_ANGLE) % 360)) % 360;
  return `hsl(${hue} ${SATURATION}% ${LIGHTNESS}%)`;
}

/**
 * Style that paints the accent along a card's bottom edge. An inset shadow keeps
 * the rounded corners intact, which a thicker border would break.
 */
export function accentBarStyle(accent?: string): CSSProperties | undefined {
  return accent
    ? { boxShadow: `inset 0 -3px 0 0 ${accent}` }
    : undefined;
}
