import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import zhCN from 'antd/locale/zh_CN';
import zhTW from 'antd/locale/zh_TW';
import enUS from 'antd/locale/en_US';
import {
  DEFAULT_LOCALE,
  LOCALES,
  type Locale,
  type MessageKey,
  type MessageParams,
  resolveLocale,
  setActiveLocale,
  translate,
} from './i18n';

export type LanguageMode = 'auto' | Locale;
export type ThemeMode = 'auto' | 'light' | 'dark';

interface UiContextValue {
  locale: Locale;
  languageMode: LanguageMode;
  setLanguageMode: (mode: LanguageMode) => void;
  themeMode: ThemeMode;
  setThemeMode: (mode: ThemeMode) => void;
  isDark: boolean;
  antdLocale: typeof zhCN;
  t: (key: MessageKey, params?: MessageParams) => string;
}

const LANGUAGE_KEY = 'tokentracker.language';
const THEME_KEY = 'tokentracker.theme';

const UiContext = createContext<UiContextValue | null>(null);

const ANTD_LOCALES = {
  en: enUS,
  'zh-CN': zhCN,
  'zh-TW': zhTW,
} as const;

function detectLocale(): Locale {
  const preferred = navigator.language || navigator.languages?.[0] || 'en';
  return resolveLocale(preferred);
}

function loadLanguageMode(): LanguageMode {
  try {
    const value = localStorage.getItem(LANGUAGE_KEY);
    if (value === 'auto') {
      return 'auto';
    }
    if (value && LOCALES.some((locale) => locale.id === value)) {
      return value as Locale;
    }
  } catch {
    // Storage is best-effort.
  }
  return 'auto';
}

function loadThemeMode(): ThemeMode {
  try {
    const value = localStorage.getItem(THEME_KEY);
    if (value === 'light' || value === 'dark' || value === 'auto') {
      return value;
    }
  } catch {
    // Storage is best-effort.
  }
  return 'auto';
}

export function UiProvider({ children }: { children: ReactNode }) {
  const [languageMode, setLanguageMode] = useState<LanguageMode>(loadLanguageMode);
  const [themeMode, setThemeMode] = useState<ThemeMode>(loadThemeMode);
  const [systemDark, setSystemDark] = useState(
    () => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false,
  );

  const locale: Locale =
    languageMode === 'auto' ? detectLocale() : languageMode;
  const isDark = themeMode === 'dark' || (themeMode === 'auto' && systemDark);

  useEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const update = (event: MediaQueryListEvent) => setSystemDark(event.matches);
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);

  useEffect(() => {
    try {
      localStorage.setItem(LANGUAGE_KEY, languageMode);
      localStorage.setItem(THEME_KEY, themeMode);
    } catch {
      // Storage is best-effort.
    }
  }, [languageMode, themeMode]);

  useEffect(() => {
    document.documentElement.lang = locale;
    document.documentElement.dataset.theme = isDark ? 'dark' : 'light';
    document.documentElement.style.colorScheme = isDark ? 'dark' : 'light';
  }, [locale, isDark]);

  // Module-level helpers (column factories, label tables) read the locale here.
  setActiveLocale(locale);

  const value = useMemo<UiContextValue>(
    () => ({
      locale,
      languageMode,
      setLanguageMode,
      themeMode,
      setThemeMode,
      isDark,
      antdLocale: ANTD_LOCALES[locale] ?? ANTD_LOCALES[DEFAULT_LOCALE],
      t: (key, params) => translate(locale, key, params),
    }),
    [locale, languageMode, themeMode, isDark],
  );

  return <UiContext.Provider value={value}>{children}</UiContext.Provider>;
}

export function useUi(): UiContextValue {
  const value = useContext(UiContext);
  if (!value) {
    throw new Error('useUi must be used inside UiProvider');
  }
  return value;
}
