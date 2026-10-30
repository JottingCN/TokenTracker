import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';

export type MoneyUnit = 'cny' | 'usd' | 'credit';

export interface MoneySettings {
  unit: MoneyUnit;
  usdRate: number;
  creditRate: number;
}

interface MoneyContextValue {
  settings: MoneySettings;
  setSettings: (patch: Partial<MoneySettings>) => void;
  unitLabel: string;
  priceSuffix: string;
  toPriceInput: (cny: number) => number;
  fromPriceInput: (value: number) => number;
  pricePrecision: number;
}

const STORAGE_KEY = 'tokentracker.money';
const DEFAULT_SETTINGS: MoneySettings = {
  unit: 'cny',
  usdRate: 7.2,
  creditRate: 0.01,
};

let current = DEFAULT_SETTINGS;
let currentLanguage: 'zh' | 'en' = 'zh';

export function setMoneyLanguage(language: 'zh' | 'en'): void {
  currentLanguage = language;
}

function normalize(value: Partial<MoneySettings> | null): MoneySettings {
  const unit =
    value?.unit === 'usd' || value?.unit === 'credit' ? value.unit : 'cny';
  const usdRate =
    typeof value?.usdRate === 'number' &&
    Number.isFinite(value.usdRate) &&
    value.usdRate > 0
      ? value.usdRate
      : DEFAULT_SETTINGS.usdRate;
  const creditRate =
    typeof value?.creditRate === 'number' &&
    Number.isFinite(value.creditRate) &&
    value.creditRate > 0
      ? value.creditRate
      : DEFAULT_SETTINGS.creditRate;
  return { unit, usdRate, creditRate };
}

function load(): MoneySettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? normalize(JSON.parse(raw) as Partial<MoneySettings>) : DEFAULT_SETTINGS;
  } catch {
    return DEFAULT_SETTINGS;
  }
}

current = typeof localStorage === 'undefined' ? DEFAULT_SETTINGS : load();

export function unitLabel(unit: MoneyUnit = current.unit): string {
  if (unit === 'usd') {
    return currentLanguage === 'zh' ? '美元（USD）' : 'US Dollar (USD)';
  }
  if (unit === 'credit') {
    return currentLanguage === 'zh' ? '积分' : 'Credits';
  }
  return currentLanguage === 'zh' ? '人民币（CNY）' : 'Chinese Yuan (CNY)';
}

export function formatMoney(
  cny: number | null | undefined,
  digits?: number,
): string {
  if (cny === null || cny === undefined || !Number.isFinite(cny)) {
    return '-';
  }

  if (current.unit === 'usd') {
    return `$${(cny / current.usdRate).toFixed(digits ?? 6)}`;
  }
  if (current.unit === 'credit') {
    const suffix = currentLanguage === 'zh' ? ' 积分' : ' credits';
    return `${(cny / current.creditRate).toFixed(digits ?? 2)}${suffix}`;
  }
  return `¥${cny.toFixed(digits ?? 6)}`;
}

export function formatCost(
  cny: number | null | undefined,
  unpricedCalls = 0,
): string {
  const formatted = formatMoney(cny);
  const suffix = currentLanguage === 'zh' ? '未定价' : 'unpriced';
  return unpricedCalls > 0
    ? `${formatted} + ${unpricedCalls} ${suffix}`
    : formatted;
}

export function formatPrice(cny: number | null | undefined): string {
  return formatMoney(cny, current.unit === 'credit' ? 2 : 4);
}

function toPriceInput(cny: number): number {
  if (current.unit === 'usd') {
    return cny / current.usdRate;
  }
  if (current.unit === 'credit') {
    return cny / current.creditRate;
  }
  return cny;
}

function fromPriceInput(value: number): number {
  if (current.unit === 'usd') {
    return value * current.usdRate;
  }
  if (current.unit === 'credit') {
    return value * current.creditRate;
  }
  return value;
}

const MoneyContext = createContext<MoneyContextValue | null>(null);

export function MoneyProvider({
  children,
  language,
}: {
  children: ReactNode;
  language: 'zh' | 'en';
}) {
  const [settings, setSettingsState] = useState<MoneySettings>(load);
  current = settings;
  setMoneyLanguage(language);

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
    } catch {
      // Storage is best-effort.
    }
  }, [settings]);

  const value = useMemo<MoneyContextValue>(
    () => ({
      settings,
      setSettings: (patch) =>
        setSettingsState((previous) => normalize({ ...previous, ...patch })),
      unitLabel: unitLabel(settings.unit),
      priceSuffix:
        currentLanguage === 'zh' ? '/ 百万 tokens' : '/ 1M tokens',
      toPriceInput,
      fromPriceInput,
      pricePrecision: settings.unit === 'credit' ? 2 : 4,
    }),
    [settings, language],
  );

  return <MoneyContext.Provider value={value}>{children}</MoneyContext.Provider>;
}

export function useMoney(): MoneyContextValue {
  const value = useContext(MoneyContext);
  if (!value) {
    throw new Error('useMoney must be used inside MoneyProvider');
  }
  return value;
}
