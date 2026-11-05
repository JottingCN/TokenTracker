import { t } from '../i18n';

const PROVIDERS: Array<[RegExp, string]> = [
  [/^deepseek/i, 'DeepSeek'],
  [/^(openai|gpt|o[134])/i, 'OpenAI'],
  [/^(anthropic|claude)/i, 'Anthropic'],
  [/^(google|gemini)/i, 'Google'],
  [/^(meta|llama)/i, 'Meta'],
  [/^kimi/i, 'Kimi'],
  [/^mimo/i, '小米 MiMo'],
  [/^minimax/i, 'MiniMax'],
  [/^glm/i, '智谱 GLM'],
];

function leafOf(model: string): string {
  const slash = model.lastIndexOf('/');
  return slash >= 0 ? model.slice(slash + 1) : model;
}

export function providerOf(model: string, override?: string | null): string {
  if (override?.trim()) {
    return localizeProvider(override.trim());
  }

  const leaf = leafOf(model);
  const provider =
    PROVIDERS.find(([pattern]) => pattern.test(leaf))?.[1] ?? '其他';
  return localizeProvider(provider);
}

function localizeProvider(provider: string): string {
  if (provider === '小米 MiMo') {
    return t('xiaomi.mimo');
  }
  if (provider === '智谱 GLM') {
    return t('zhipu.glm');
  }
  if (provider === '其他') {
    return t('other');
  }
  return provider;
}

export function leafModelName(model: string): string {
  return leafOf(model);
}

// The provider prefix is dropped only when a usable name remains: "deepseek-flash"
// becomes "Flash", but "glm-5" would collapse to "5" and is kept as-is.
export function shortModelName(model: string): string {
  const leaf = leafOf(model);
  const stripped = leaf.replace(
    /^(deepseek|openai|anthropic|google|meta|kimi|mimo|minimax|glm)[-/:]/i,
    '',
  );
  if (!stripped || /^\d/.test(stripped)) {
    return leaf;
  }

  return stripped
    .split(/[-_]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}
