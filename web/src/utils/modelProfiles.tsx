import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from 'react';
import type { ReactNode } from 'react';
import type { ModelSettingsRow } from '../types';

interface ModelProfilesValue {
  profiles: Record<string, ModelSettingsRow>;
  profileOf: (model: string) => ModelSettingsRow | undefined;
  refresh: () => Promise<void>;
}

const ModelProfilesContext = createContext<ModelProfilesValue>({
  profiles: {},
  profileOf: () => undefined,
  refresh: async () => {},
});

// Model icons and provider names are presentation metadata that every view needs
// (sidebar groups, model cells, session tabs, call details). Loading them once
// into a context keeps the per-payload data free of icon blobs.
export function ModelProfilesProvider({ children }: { children: ReactNode }) {
  const [profiles, setProfiles] = useState<Record<string, ModelSettingsRow>>({});

  const refresh = useCallback(async () => {
    try {
      const response = await fetch('/api/models', { cache: 'no-store' });
      if (!response.ok) {
        return;
      }
      const rows = (await response.json()) as ModelSettingsRow[];
      const next: Record<string, ModelSettingsRow> = {};
      for (const row of rows) {
        next[row.model.toLowerCase()] = row;
      }
      setProfiles(next);
    } catch {
      // Older servers have no /api/models; the derived provider/icon is used.
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const value = useMemo<ModelProfilesValue>(
    () => ({
      profiles,
      profileOf: (model: string) => profiles[model.toLowerCase()],
      refresh,
    }),
    [profiles, refresh],
  );

  return (
    <ModelProfilesContext.Provider value={value}>
      {children}
    </ModelProfilesContext.Provider>
  );
}

export function useModelProfiles(): ModelProfilesValue {
  return useContext(ModelProfilesContext);
}
