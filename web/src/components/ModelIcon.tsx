import { Tooltip } from 'antd';
import type { ModelProfile } from '../types';
import { providerOf } from '../utils/model';
import { useModelProfiles } from '../utils/modelProfiles';

const PROVIDER_STYLES: Record<string, { color: string; letter: string }> = {
  DeepSeek: { color: '#4D6BFE', letter: 'D' },
  OpenAI: { color: '#10A37F', letter: 'O' },
  Anthropic: { color: '#D97757', letter: 'A' },
  Google: { color: '#4285F4', letter: 'G' },
  Meta: { color: '#0866FF', letter: 'M' },
  Kimi: { color: '#111827', letter: 'K' },
  '小米 MiMo': { color: '#FF6900', letter: 'M' },
  'Xiaomi MiMo': { color: '#FF6900', letter: 'M' },
  MiniMax: { color: '#E5484D', letter: 'M' },
  '智谱 GLM': { color: '#3859FF', letter: 'G' },
  'Zhipu GLM': { color: '#3859FF', letter: 'G' },
};

export const MODEL_ICON_COLORS = [
  '#4D6BFE',
  '#10A37F',
  '#D97757',
  '#4285F4',
  '#0866FF',
  '#111827',
  '#FF6900',
  '#E5484D',
  '#3859FF',
  '#8C8C8C',
];

interface ModelIconProps {
  model: string;
  size?: number;
  tooltip?: boolean;
  // Lets the settings editor preview unsaved values without touching the shared
  // profile store. `undefined` means "look it up", `null` means "no profile".
  profile?: ModelProfile | null;
}

export function ModelIcon({
  model,
  size = 16,
  tooltip = true,
  profile: profileOverride,
}: ModelIconProps) {
  const { profileOf } = useModelProfiles();
  const profile =
    profileOverride === undefined ? profileOf(model) : profileOverride;
  const provider = providerOf(model, profile?.provider);
  const fallback = PROVIDER_STYLES[provider] ?? {
    color: '#8C8C8C',
    // Custom providers (e.g. "DeepSeek 官方") get their own initial instead of ?.
    letter: (provider.match(/[\p{L}\p{N}]/u)?.[0] ?? '?').toUpperCase(),
  };
  const radius = Math.max(3, Math.round(size * 0.25));

  const icon =
    profile?.iconKind === 'image' && profile.iconImage ? (
      <img
        alt={provider}
        src={profile.iconImage}
        width={size}
        height={size}
        style={{
          display: 'inline-block',
          flex: '0 0 auto',
          width: size,
          height: size,
          borderRadius: radius,
          objectFit: 'cover',
        }}
      />
    ) : (
      <span
        aria-label={provider}
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          flex: '0 0 auto',
          width: size,
          height: size,
          borderRadius: radius,
          background: profile?.iconColor ?? fallback.color,
          color: '#fff',
          fontSize: Math.max(9, Math.round(size * 0.58)),
          fontWeight: 600,
          lineHeight: 1,
        }}
      >
        {(profile?.iconLetter ?? fallback.letter).slice(0, 2)}
      </span>
    );

  return tooltip ? <Tooltip title={provider}>{icon}</Tooltip> : icon;
}
