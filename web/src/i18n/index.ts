import type { MessageKey } from './en';
import { en } from './en';
import { zhCN } from './zh-CN';
import { zhTW } from './zh-TW';

export type { MessageKey };

// Locales are the VS Code language identifiers so the same tag can be reused by
// the extension and by the language picker.
export type Locale = 'en' | 'zh-CN' | 'zh-TW';

export interface LocaleInfo {
  id: Locale;
  label: string;
  /** Matches navigator.language / vscode.env.language prefixes. */
  aliases: readonly string[];
}

export const LOCALES: readonly LocaleInfo[] = [
  { id: 'en', label: 'English', aliases: ['en'] },
  { id: 'zh-CN', label: '简体中文', aliases: ['zh-cn', 'zh-hans', 'zh'] },
  { id: 'zh-TW', label: '繁體中文', aliases: ['zh-tw', 'zh-hant', 'zh-hk', 'zh-mo'] },
];

export const DEFAULT_LOCALE: Locale = 'en';

const CATALOGS: Record<Locale, Record<MessageKey, string>> = {
  en,
  'zh-CN': zhCN,
  'zh-TW': zhTW,
};

export type MessageParams = Record<string, string | number>;

/** Resolves a BCP-47 tag such as `zh-Hant-TW` onto a shipped catalog. */
export function resolveLocale(tag: string | null | undefined): Locale {
  const normalized = (tag ?? '').trim().toLowerCase();
  if (!normalized) {
    return DEFAULT_LOCALE;
  }

  for (const locale of LOCALES) {
    if (locale.aliases.some((alias) => normalized === alias)) {
      return locale.id;
    }
  }
  // Longest-prefix match so `zh-hant-tw` still lands on zh-TW and `en-gb` on en.
  const parts = normalized.split('-');
  for (let length = parts.length; length > 0; length -= 1) {
    const candidate = parts.slice(0, length).join('-');
    for (const locale of LOCALES) {
      if (locale.aliases.includes(candidate)) {
        return locale.id;
      }
    }
  }
  return DEFAULT_LOCALE;
}

/** Maps a UI locale onto the tag used by Intl formatters. */
export function intlLocale(locale: Locale): 'en-US' | 'zh-CN' | 'zh-TW' {
  return locale === 'zh-CN' ? 'zh-CN' : locale === 'zh-TW' ? 'zh-TW' : 'en-US';
}

function interpolate(template: string, params?: MessageParams): string {
  if (!params) {
    return template;
  }
  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in params ? String(params[name]) : match,
  );
}

export function translate(
  locale: Locale,
  key: MessageKey,
  params?: MessageParams,
): string {
  const catalog = CATALOGS[locale] ?? en;
  // A missing translation falls back to English instead of showing the raw key.
  const template = catalog[key] ?? en[key] ?? key;
  return interpolate(template, params);
}

// Module-level helpers (column definitions, label tables) run outside React and
// need the active locale; the provider keeps it in sync on every render.
let activeLocale: Locale = DEFAULT_LOCALE;

export function setActiveLocale(locale: Locale): void {
  activeLocale = locale;
}

export function getActiveLocale(): Locale {
  return activeLocale;
}

export function t(key: MessageKey, params?: MessageParams): string {
  return translate(activeLocale, key, params);
}
